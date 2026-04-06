using System;
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
    ///
    /// <para>
    /// プロキシプロセス（--mcp-proxy）に登録される。
    /// ローカルの McpConnector と本体の MessageBus を NDJSON パイプで繋ぐ。
    /// </para>
    ///
    /// <para>
    /// 接続シーケンス:
    ///   1. 本体プロセスが起動済みかを確認（パイプへの接続試行）
    ///   2. 接続できなければ本体プロセスを自動起動してリトライ
    ///   3. 接続後は双方向の中継ループを開始する
    /// </para>
    ///
    /// <para>
    /// 切断時の動作:
    ///   本体プロセスが終了したときはパイプが切断される。
    ///   CancellationToken にキャンセルを流してプロキシプロセス全体を終了させる。
    /// </para>
    /// </summary>
    public sealed class PipeClientConnector : IConnector
    {
        // -------------------------------------------------------------------
        // フィールド
        // -------------------------------------------------------------------

        private readonly string            _pipeName;
        private readonly string?           _serverExePath;  // 自動起動用
        private readonly TimeSpan          _connectTimeout;

        private Action<string>?   _publish;
        private StreamWriter?     _writer;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        // -------------------------------------------------------------------
        // コンストラクタ
        // -------------------------------------------------------------------

        /// <param name="pipeName">接続先のパイプ名。</param>
        /// <param name="serverExePath">
        /// 本体プロセスが起動していない場合に自動起動する EXE のパス。
        /// null の場合は自動起動しない。
        /// </param>
        /// <param name="connectTimeout">接続タイムアウト（デフォルト10秒）。</param>
        public PipeClientConnector(
            string   pipeName,
            string?  serverExePath  = null,
            TimeSpan connectTimeout = default)
        {
            _pipeName       = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _serverExePath  = serverExePath;
            _connectTimeout = connectTimeout == default ? TimeSpan.FromSeconds(10) : connectTimeout;
        }

        // -------------------------------------------------------------------
        // IConnector
        // -------------------------------------------------------------------

        public string Name => "PipeClient";

        public Action<string> Publish
        {
            set => _publish = value;
        }

        /// <summary>
        /// ローカルバス（McpConnector）からの送信をパイプ経由で本体に転送する。
        /// </summary>
        public void Deliver(string messageJson)
        {
            if (_disposed || _writer == null) return;
            _ = SendAsync(messageJson);
        }

        // -------------------------------------------------------------------
        // 接続・ループ
        // -------------------------------------------------------------------

        /// <summary>
        /// 本体プロセスに接続し、双方向中継ループを開始する。
        /// Program.cs の RunMcpProxy から await される。
        /// </summary>
        public async Task RunAsync(CancellationToken ct)
        {
            var pipe = await ConnectWithRetryAsync(ct).ConfigureAwait(false);
            if (pipe == null) return;   // キャンセルまたは自動起動失敗

            AppLog.Log("INFO", "PipeClientConnector", "本体プロセスに接続しました");

            _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

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
                _writer = null;
                pipe.Dispose();
                AppLog.Log("INFO", "PipeClientConnector", "本体プロセスとの接続が切断されました");
            }
        }

        // -------------------------------------------------------------------
        // 接続（自動起動・リトライつき）
        // -------------------------------------------------------------------

        private async Task<NamedPipeClientStream?> ConnectWithRetryAsync(CancellationToken ct)
        {
            const int MaxAttempts = 3;
            var timeout = (int)_connectTimeout.TotalMilliseconds;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested) return null;

                var pipe = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    AppLog.Log("INFO", "PipeClientConnector",
                        $"接続試行 {attempt + 1}/{MaxAttempts}: \\\\.\\pipe\\{_pipeName}");

                    await pipe.ConnectAsync(timeout, ct).ConfigureAwait(false);
                    return pipe; // 接続成功
                }
                catch (TimeoutException)
                {
                    pipe.Dispose();
                    AppLog.Log("WARN", "PipeClientConnector", "接続タイムアウト");
                }
                catch (OperationCanceledException) { pipe.Dispose(); return null; }
                catch (Exception ex) { pipe.Dispose(); AppLog.Log("WARN", "PipeClientConnector", ex.Message); }

                // 1回目の失敗後に自動起動を試みる
                if (attempt == 0 && _serverExePath != null)
                {
                    TryLaunchServer();
                    AppLog.Log("INFO", "PipeClientConnector", "本体プロセスを起動しています...");
                    await Task.Delay(2000, ct).ConfigureAwait(false); // 起動を少し待つ
                }
                else if (attempt > 0)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }

            AppLog.Log("ERROR", "PipeClientConnector",
                "本体プロセスへの接続に失敗しました。" +
                $"WebView2AppHost.exe が起動していることを確認してください。");
            return null;
        }

        private void TryLaunchServer()
        {
            if (string.IsNullOrEmpty(_serverExePath) || !File.Exists(_serverExePath)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = _serverExePath,
                    UseShellExecute = true,  // GUI アプリとして起動
                });
                AppLog.Log("INFO", "PipeClientConnector", $"起動: {_serverExePath}");
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "PipeClientConnector.LaunchServer", ex.Message);
            }
        }

        // -------------------------------------------------------------------
        // 送信
        // -------------------------------------------------------------------

        private async Task SendAsync(string json)
        {
            if (_writer == null) return;
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "PipeClientConnector.Send", ex.Message);
            }
            finally { _writeLock.Release(); }
        }

        // -------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writeLock.Dispose();
        }
    }
}
