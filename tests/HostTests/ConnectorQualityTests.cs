using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebView2AppHost;

namespace HostTests
{
    internal static class ConnectorQualityTests
    {
        public static void RunAll()
        {
            Console.WriteLine("--- Connector Quality & Thread-Safety Tests ---");
            TestDllConnectorParallelAccess().Wait();
            TestPipeServerBackpressure().Wait();
            Console.WriteLine("  Connector quality tests passed.");
        }

        /// <summary>
        /// DllConnector に対して並列にメッセージを送り込み、スレッド安全性を検証する。
        /// </summary>
        private static async Task TestDllConnectorParallelAccess()
        {
            Console.WriteLine("  [Test] DllConnector Parallel Access...");
            var dll = new DllConnector();
            
            // ダミーの Publish アクション
            dll.Publish = json => { };

            // 1. ロード処理と配信処理を同時に走らせる
            var cts = new CancellationTokenSource(2000);
            var tasks = new List<Task>();

            // 配信スレッド群
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        // 存在しないエイリアスや "Host" 宛のメッセージを大量に送る
                        dll.Deliver("{\"jsonrpc\":\"2.0\",\"method\":\"Host.Dummy\",\"params\":[],\"id\":1}");
                        dll.Deliver("{\"jsonrpc\":\"2.0\",\"method\":\"Unknown.Dummy\",\"params\":[],\"id\":2}");
                    }
                }));
            }

            // 初期化（コレクション変更）スレッド
            tasks.Add(Task.Run(() =>
            {
                int count = 0;
                while (!cts.IsCancellationRequested)
                {
                    // 頻繁に初期化（内部辞書のクリアと構築）を試みる
                    // ※本来は一回限りだが、スレッド安全性の隙を突くために繰り返す
                    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                        "{\"connectors\": [{\"type\":\"dll\", \"path\":\"dummy.dll\", \"alias\":\"Test" + (count++) + "\"}]}"));
                    var config = AppConfig.Load(stream) ?? new AppConfig();
                    dll.Initialize(config);
                    Thread.Sleep(10);
                }
            }));

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                throw new Exception("DllConnector スレッド安全性テスト失敗: " + ex.Message, ex);
            }
            Console.WriteLine("    DllConnector parallel access test passed.");
        }

        /// <summary>
        /// PipeServerConnector の送信キューが溢れた際の挙動を検証する。
        /// </summary>
        private static async Task TestPipeServerBackpressure()
        {
            Console.WriteLine("  [Test] PipeServerConnector Backpressure...");
            string pipeName = "test-backpressure-" + Guid.NewGuid();
            using var server = new PipeServerConnector(pipeName);
            
            int receivedCount = 0;
            server.Publish = json => { };
            server.Start();

            // クライアントを接続させる
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);
            
            // セッションが認識されるのを少し待つ
            await Task.Delay(200);

            // サーバーから大量のメッセージを送る
            int sendCount = 500;
            Console.WriteLine($"    Sending {sendCount} messages...");
            for (int i = 0; i < sendCount; i++)
            {
                server.Deliver("{\"msg\":" + i + "}");
            }

            // クライアントで読み取る
            Console.WriteLine("    Reading messages from client...");
            using var reader = new System.IO.StreamReader(client);
            var readTask = Task.Run(() =>
            {
                try
                {
                    while (receivedCount < sendCount)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;
                        receivedCount++;
                    }
                }
                catch (Exception ex) { Console.WriteLine("    [Error] Client Read: " + ex.Message); }
            });

            if (await Task.WhenAny(readTask, Task.Delay(5000)) != readTask)
            {
                throw new Exception($"PipeServerConnector メッセージドロップ検出: 送信={sendCount}, 受信={receivedCount} (タイムアウト)");
            }

            if (receivedCount != sendCount)
            {
                throw new Exception($"PipeServerConnector メッセージ数不一致: 送信={sendCount}, 受信={receivedCount}");
            }

            Console.WriteLine($"    PipeServerConnector backpressure test passed (Received {receivedCount}/{sendCount}).");
        }
    }
}
