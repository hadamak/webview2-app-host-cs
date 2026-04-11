using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebView2AppHost
{
    /// <summary>
    /// Named Pipe サーバーとして待ち受け、接続したプロキシプロセスと
    /// メッセージを双方向に中継するコネクター。
    ///
    /// <para>
    /// 本体プロセス（WebView2AppHost.exe）に登録される。
    /// --mcp-proxy プロセスが接続してくるのを受け入れ、
    /// パイプ経由で届いたメッセージをバスに流し、
    /// バスからの配信をパイプ経由でプロキシに送り返す。
    /// </para>
    ///
    /// <para>
    /// プロトコル: NDJSON（1メッセージ = 1行）。サイドカーと同じ形式。
    /// </para>
    ///
    /// <para>
    /// 複数同時接続: 各クライアントを独立した ClientSession で管理する。
    /// あるクライアントが切断しても他のクライアントへの配信は継続する。
    /// </para>
    /// </summary>
    public sealed class PipeServerConnector : IConnector
    {
        // -------------------------------------------------------------------
        // フィールド
        // -------------------------------------------------------------------

        private readonly string             _pipeName;
        private readonly CancellationTokenSource _internalCts;
        private readonly CancellationToken       _combinedToken;

        private Action<string>?  _publish;
        private readonly object  _sessionsLock = new object();
        private readonly List<ClientSession> _sessions = new List<ClientSession>();
        private bool _disposed;

        // -------------------------------------------------------------------
        // コンストラクタ
        // -------------------------------------------------------------------

        public PipeServerConnector(
            string pipeName,
            CancellationToken shutdownToken = default)
        {
            _pipeName   = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _internalCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            _combinedToken = _internalCts.Token;
        }

        // -------------------------------------------------------------------
        // IConnector
        // -------------------------------------------------------------------

        public string Name => "PipeServer";

        public Action<string> Publish
        {
            set => _publish = value;
        }

        /// <summary>
        /// バスからの配信を全接続クライアントに送る。
        /// </summary>
        public void Deliver(string messageJson, Dictionary<string, object>? messageDict)
        {
            if (_disposed) return;

            List<ClientSession> snapshot;
            lock (_sessionsLock) snapshot = new List<ClientSession>(_sessions);

            foreach (var s in snapshot)
                s.Send(messageJson, _combinedToken);
        }

        // -------------------------------------------------------------------
        // 受付ループ起動
        // -------------------------------------------------------------------

        /// <summary>
        /// Named Pipe サーバーを開始し、クライアントの接続を非同期に受け入れる。
        /// ConnectorFactory から呼ばれる。
        /// </summary>
        public void Start()
        {
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            AppLog.Log(AppLog.LogLevel.Info, $"PipeServerConnector",
                $"Named Pipe 待機中: \\\\.\\pipe\\{_pipeName}");

            while (!_combinedToken.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(_combinedToken).ConfigureAwait(false);
                    AppLog.Log(AppLog.LogLevel.Info, "PipeServerConnector", "プロキシ接続を受け入れました");

                    var session = new ClientSession(pipe, json => _publish?.Invoke(json));
                    lock (_sessionsLock) _sessions.Add(session);

                    // 各セッションは独立して動作し、切断時に自分をリストから削除する
                    _ = session.RunAsync(_combinedToken).ContinueWith(_ =>
                    {
                        lock (_sessionsLock) _sessions.Remove(session);
                        AppLog.Log(AppLog.LogLevel.Info, "PipeServerConnector", "プロキシ切断");
                    });
                }
                catch (OperationCanceledException) { pipe.Dispose(); break; }
                catch (Exception ex)
                {
                    AppLog.Log(AppLog.LogLevel.Warn, "PipeServerConnector.Accept", ex.Message);
                    pipe.Dispose();
                    try { await Task.Delay(1000, _combinedToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        // -------------------------------------------------------------------
        // クライアントセッション
        // -------------------------------------------------------------------

        private sealed class ClientSession
        {
            private readonly NamedPipeServerStream    _pipe;
            private readonly Action<string>           _onReceive;
            private readonly BlockingCollection<string> _sendQueue =
                new BlockingCollection<string>(boundedCapacity: 1024);

            public ClientSession(NamedPipeServerStream pipe, Action<string> onReceive)
            {
                _pipe      = pipe;
                _onReceive = onReceive;
            }

            public async Task RunAsync(CancellationToken ct)
            {
                // 送信ループを別スレッドで動かす（ブロッキング書き込みでも受信をブロックしない）
                var sendThread = new System.Threading.Thread(() =>
                {
                    var writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
                    try
                    {
                        foreach (var line in _sendQueue.GetConsumingEnumerable(ct))
                        {
                            if (!_pipe.IsConnected) break;
                            writer.WriteLine(line);
                        }
                    }
                    catch { /* パイプ切断 */ }
                })
                { IsBackground = true, Name = "PipeSession.Send" };
                sendThread.Start();

                // 受信ループ
                using var reader = new StreamReader(_pipe, new UTF8Encoding(false));
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!string.IsNullOrWhiteSpace(line))
                            _onReceive(line);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    AppLog.Log(AppLog.LogLevel.Warn, "PipeServerConnector.Session", ex.Message);
                }
                finally
                {
                    _sendQueue.CompleteAdding();
                    _pipe.Dispose();
                    _sendQueue.Dispose();
                }
            }

            public void Send(string json, CancellationToken ct)
            {
                if (!_pipe.IsConnected || _sendQueue.IsAddingCompleted) return;
                try
                {
                    // TryAdd ではなく Add を使うことで、キューが一杯の場合は呼び出し元を待機させる（バックプレッシャ）。
                    // これによりメッセージのドロップを防ぐ。
                    _sendQueue.Add(json, ct);
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { } // CompleteAdding 済み
                catch (Exception ex)
                {
                    AppLog.Log(AppLog.LogLevel.Warn, "PipeServerConnector.Send", $"キュー追加失敗: {ex.Message}");
                }
            }
        }

        // -------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 内部 CTS をキャンセルし、WaitForConnectionAsync 中のパイプを中断させる。
            // これにより AcceptLoopAsync が OperationCanceledException を受け取り、
            // 待機中のパイプを Dispose する。
            try { _internalCts.Cancel(); } catch (ObjectDisposedException) { }

            lock (_sessionsLock) _sessions.Clear();

            // 各セッションは RunAsync 内で pipe.Dispose() する。
            // _internalCts のキャンセルにより RunAsync も終了に向かう。
            _internalCts.Dispose();
        }
    }
}
