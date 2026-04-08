using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebView2AppHost
{
    /// <summary>
    /// Named Pipe クライアントとして本体プロセスに接続し、
    /// プロキシプロセスのローカルバスと本体バスを中継するコネクター。
    /// </summary>
    public sealed class PipeClientConnector : IConnector
    {
        private readonly string            _pipeName;
        private readonly string?           _serverExePath;
        private readonly TimeSpan          _connectTimeout;

        private Action<string>?   _publish;
        private readonly BlockingCollection<string> _sendQueue = new BlockingCollection<string>(1024);
        private bool _disposed;

        public PipeClientConnector(
            string   pipeName,
            string?  serverExePath  = null,
            TimeSpan connectTimeout = default)
        {
            _pipeName       = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _serverExePath  = serverExePath;
            _connectTimeout = connectTimeout == default ? TimeSpan.FromSeconds(10) : connectTimeout;
        }

        public string Name => "PipeClient";

        public Action<string> Publish
        {
            set => _publish = value;
        }

        /// <summary>
        /// ローカルバス（McpConnector）からの送信をキューに追加する。
        /// </summary>
        public void Deliver(string messageJson)
        {
            if (_disposed || _sendQueue.IsAddingCompleted) return;
            try
            {
                // 送信順序を保証するため、即座にキューへ入れる（バックプレッシャあり）
                _sendQueue.Add(messageJson);
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "PipeClientConnector.Deliver", $"キュー追加失敗: {ex.Message}");
            }
        }

        public async Task RunAsync(CancellationToken ct)
        {
            var pipe = await ConnectWithRetryAsync(ct).ConfigureAwait(false);
            if (pipe == null) return;

            AppLog.Log("INFO", "PipeClientConnector", "本体プロセスに接続しました");

            using (pipe)
            {
                // 送信ループタスクを開始
                var sendTask = Task.Run(() => RunSendLoop(pipe, ct), ct);

                // 受信ループ（本体バスからの配信 → ローカルバスへ）
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!string.IsNullOrWhiteSpace(line))
                            _publish?.Invoke(line);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    AppLog.Log("WARN", "PipeClientConnector.Receive", ex.Message);
                }
                finally
                {
                    _sendQueue.CompleteAdding();
                    // パイプ切断時に送信タスクの完了を待つ（短時間）
                    await Task.WhenAny(sendTask, Task.Delay(1000, ct)).ConfigureAwait(false);
                    AppLog.Log("INFO", "PipeClientConnector", "本体プロセスとの接続が切断されました");
                }
            }
        }

        private void RunSendLoop(NamedPipeClientStream pipe, CancellationToken ct)
        {
            try
            {
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                foreach (var line in _sendQueue.GetConsumingEnumerable(ct))
                {
                    if (!pipe.IsConnected) break;
                    writer.WriteLine(line);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                AppLog.Log("WARN", "PipeClientConnector.Send", ex.Message);
            }
        }

        private async Task<NamedPipeClientStream?> ConnectWithRetryAsync(CancellationToken ct)
        {
            const int MaxAttempts = 3;
            var timeout = (int)_connectTimeout.TotalMilliseconds;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested) return null;

                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    AppLog.Log("INFO", "PipeClientConnector", $"接続試行 {attempt + 1}/{MaxAttempts}: \\\\.\\pipe\\{_pipeName}");
                    await pipe.ConnectAsync(timeout, ct).ConfigureAwait(false);
                    return pipe;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    pipe.Dispose();
                    if (attempt == 0 && _serverExePath != null)
                    {
                        TryLaunchServer();
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                    }
                    else if (attempt < MaxAttempts - 1)
                    {
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                    }
                }
            }
            return null;
        }

        private void TryLaunchServer()
        {
            if (string.IsNullOrEmpty(_serverExePath) || !File.Exists(_serverExePath)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _serverExePath, UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.Log("WARN", "PipeClientConnector.Launch", ex.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sendQueue.CompleteAdding();
            _sendQueue.Dispose();
        }
    }
}
