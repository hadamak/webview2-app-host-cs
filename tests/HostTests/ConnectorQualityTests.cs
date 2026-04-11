using System;
using System.Text;
using Xunit;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebView2AppHost;

namespace HostTests
{
    public class ConnectorQualityTests
    {
        [Fact]
        public async Task TestDllConnectorParallelAccess()
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
                        dll.Deliver("{\"jsonrpc\":\"2.0\",\"method\":\"Host.Dummy\",\"params\":[],\"id\":1}", null);
                        dll.Deliver("{\"jsonrpc\":\"2.0\",\"method\":\"Unknown.Dummy\",\"params\":[],\"id\":2}", null);
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

        [Fact(Skip = "Flaky pipe test")]
        public async Task TestPipeServerBackpressure()
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
                server.Deliver("{\"msg\":" + i + "}", null);
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

        [Fact(Skip = "Flaky pipe test")]
        public async Task TestPipeClientRetryAndIntegration()
        {
            string pipeName = "test-pipeclient-" + Guid.NewGuid();
            var receivedMessages = new List<string>();
            var clientConnector = new PipeClientConnector(pipeName, connectTimeout: TimeSpan.FromSeconds(2));
            clientConnector.Publish = json => receivedMessages.Add(json);

            var tcs = new TaskCompletionSource<bool>();
            var serverTask = Task.Run(async () =>
            {
                using var server = new System.IO.Pipes.NamedPipeServerStream(pipeName, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                tcs.SetResult(true);
                await server.WaitForConnectionAsync();
                
                try
                {
                    using var writer = new System.IO.StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                    await writer.WriteLineAsync("{\"msg\":\"from-server\"}");

                    using var reader = new System.IO.StreamReader(server, new UTF8Encoding(false));
                    var line = await reader.ReadLineAsync();
                    Assert.Equal("{\"msg\":\"from-client\"}", line);
                }
                catch (ObjectDisposedException) { }
            });

            await tcs.Task;
            
            var cts = new CancellationTokenSource(5000);
            var runTask = clientConnector.RunAsync(cts.Token);
            
            await Task.Delay(500);
            clientConnector.Deliver("{\"msg\":\"from-client\"}", null);
            
            await Task.Delay(500);
            
            Assert.Contains(receivedMessages, m => m.Contains("from-server"));
            
            cts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { }
            clientConnector.Dispose();
        }

        [Fact]
        public void TestDllConnector_SubscribeEvents_DispatchDynamicEvent()
        {
            var dll = new DllConnector();
            
            bool published = false;
            dll.Publish = json => 
            {
                if (json.Contains("OnTestEvent")) published = true;
            };

            var method = typeof(DllConnector).GetMethod("DispatchDynamicEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (method != null)
            {
                method.Invoke(dll, new object[] { "Test", "OnTestEvent", new string[] { "value" }, new object[] { new { Value = 42 } } });
            }

            Assert.True(published, "DispatchDynamicEvent should invoke Publish");
        }

#if !SECURE_OFFLINE
        [Fact]
        public async Task TestSidecar_RestartCount_UpperLimit()
        {
            var entry = new SidecarEntry { Alias = "Failer", Executable = "cmd", Args = new[] { "/c", "exit", "1" } };
            var sidecar = new SidecarConnector(entry, CancellationToken.None);
            
            var field = typeof(SidecarConnector).GetField("_restartCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            var tcs = new TaskCompletionSource<bool>();
            sidecar.Publish = json => {
                if (json.Contains("\"error\""))
                    tcs.TrySetResult(true);
            };
            
            var request = @"{""jsonrpc"":""2.0"",""id"":1,""method"":""Failer.Fail""}";
            sidecar.Deliver(request, null);
            
            await Task.Delay(3500);
            
            int count = (int)(field?.GetValue(sidecar) ?? 0);
            Assert.True(count <= 5, $"Restart count should not exceed 5, got {count}");
            
            sidecar.Dispose();
        }
#endif
    }
}
