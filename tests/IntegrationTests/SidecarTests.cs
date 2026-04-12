using System;
using Xunit;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WebView2AppHost;
using System.Reflection;
using System.Diagnostics;

namespace HostTests
{
    public class SidecarTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();
        private readonly List<SidecarConnector> _sidecars = new List<SidecarConnector>();
        private readonly CancellationTokenSource _globalCts = new CancellationTokenSource();

        public void Dispose()
        {
            _globalCts.Cancel();
            foreach (var sidecar in _sidecars)
            {
                try { sidecar.Dispose(); } catch { }
            }
            foreach (var file in _tempFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            _globalCts.Dispose();
        }

        private string CreateTempFile(string content)
        {
            var path = Path.Combine(Path.GetTempPath(), $"test_sidecar_{Guid.NewGuid():N}.js");
            File.WriteAllText(path, content);
            _tempFiles.Add(path);
            return path;
        }

        private static bool IsNodeAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo("node", "--version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    return p != null && p.WaitForExit(2000) && p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        [Fact]
        public async Task TestStreamingModeAsync()
        {
            if (!IsNodeAvailable()) return;

            // stdin をそのまま stdout に流すスクリプト
            var scriptPath = CreateTempFile("process.stdin.on('data', d => process.stdout.write(d));");
            var entry = new SidecarEntry { Alias = "Echo", Executable = "node", Args = new[] { scriptPath }, Mode = "streaming" };

            using (var testCts = new CancellationTokenSource(5000))
            {
                var sidecar = new SidecarConnector(entry, _globalCts.Token);
                _sidecars.Add(sidecar);

                var tcs = new TaskCompletionSource<string>(TaskContinuationOptions.RunContinuationsAsynchronously);
                testCts.Token.Register(() => tcs.TrySetCanceled());

                sidecar.Publish = json => tcs.TrySetResult(json);
                sidecar.Start();

                // SidecarConnector は "Alias.Method" 形式の JSON-RPC 2.0 メッセージのみを受け付ける
                var msg = "{\"jsonrpc\":\"2.0\",\"method\":\"Echo.Test\",\"params\":{\"val\":123}}";
                sidecar.Deliver(msg, null);

                var result = await tcs.Task;
                Assert.Contains("\"method\":\"Echo.Test\"", result);
            }
        }

        [Fact]
        public async Task TestRestartOnFailureAsync()
        {
            if (!IsNodeAvailable()) return;

            var marker = Path.Combine(Path.GetTempPath(), $"restart_marker_{Guid.NewGuid():N}");
            _tempFiles.Add(marker);

            // 1回目は即終了、2回目以降は動作するスクリプト
            var scriptPath = CreateTempFile($@"
                const fs = require('fs');
                if (!fs.existsSync('{marker.Replace("\\", "\\\\")}')) {{
                    fs.writeFileSync('{marker.Replace("\\", "\\\\")}', '1');
                    process.exit(1);
                }}
                process.stdin.on('data', d => process.stdout.write(d));
            ");

            var entry = new SidecarEntry { Alias = "Restarter", Executable = "node", Args = new[] { scriptPath }, Mode = "streaming" };
            
            using (var testCts = new CancellationTokenSource(15000))
            {
                var sidecar = new SidecarConnector(entry, _globalCts.Token);
                _sidecars.Add(sidecar);

                var tcs = new TaskCompletionSource<string>(TaskContinuationOptions.RunContinuationsAsynchronously);
                testCts.Token.Register(() => tcs.TrySetCanceled());

                sidecar.Publish = json => { if (json.Contains("TargetMethod")) tcs.TrySetResult(json); };
                sidecar.Start();

                // 再起動を待ちながらメッセージを送信（指数バックオフがあるため複数回試行）
                string result = null;
                var msg = "{\"jsonrpc\":\"2.0\",\"method\":\"Restarter.TargetMethod\"}";
                
                for (int i = 0; i < 20; i++)
                {
                    sidecar.Deliver(msg, null);
                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(1000, testCts.Token));
                    if (completedTask == tcs.Task)
                    {
                        result = await tcs.Task;
                        break;
                    }
                }

                Assert.NotNull(result);
                Assert.Contains("TargetMethod", result);
            }
        }

        [Fact]
        public async Task TestRestartLimitAsync()
        {
            if (!IsNodeAvailable()) return;

            var restartSignal = new ManualResetEventSlim(false);
            var entry = new SidecarEntry
            {
                Alias = "Suicide",
                Executable = "node",
                Args = new[] { "-e", "process.exit(1)" },
                Mode = "streaming"
            };
            var sidecar = new SidecarConnector(entry, _globalCts.Token);
            _sidecars.Add(sidecar);

            var countField = typeof(SidecarConnector)
                .GetField("_restartCount", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(countField);

            // _restartCount が 1 以上になったらシグナルを立てる
            // SidecarConnector は OnExited 内でカウンタをインクリメントするため、
            // ポーリングで確認する（最大 5 秒）
            sidecar.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int count = 0;
            while (!cts.IsCancellationRequested)
            {
                count = (int)(countField!.GetValue(sidecar) ?? 0);
                if (count >= 1) break;
                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }

            Assert.True(count >= 1, $"Restart count should be >= 1 within 5 seconds, actual: {count}");
            Assert.True(count <= 5, $"Restart count should not exceed the limit of 5, actual: {count}");
        }

        [Fact]
        public void TestSidecar_IsForMe_Validation()
        {
            var sidecar = new SidecarConnector(new SidecarEntry { Alias = "MySidecar" });
            var method = typeof(SidecarConnector).GetMethod("IsForMe", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            
            bool IsForMe(string json) => (bool)method.Invoke(sidecar, new object[] { json, null })!;

            Assert.True(IsForMe("{\"jsonrpc\":\"2.0\",\"method\":\"MySidecar.Do\"}"));
            Assert.True(IsForMe("{\"jsonrpc\":\"2.0\",\"method\":\"mysidecar.Test\"}")); // Case-insensitive
            Assert.False(IsForMe("{\"jsonrpc\":\"2.0\",\"method\":\"Other.Do\"}"));
            Assert.False(IsForMe("{\"method\":\"MySidecar.Do\"}")); // No jsonrpc 2.0
        }

        [Fact]
        public async Task TestSidecar_Lifecycle_StartStop()
        {
            if (!IsNodeAvailable()) return;

            var entry = new SidecarEntry { Alias = "Life", Executable = "node", Args = new[] { "-e", "setInterval(()=>{},1000)" }, Mode = "streaming" };
            var sidecar = new SidecarConnector(entry, _globalCts.Token);
            _sidecars.Add(sidecar);

            sidecar.Start();

            Process proc = null;
            var procField = typeof(SidecarConnector).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(procField);
            
            // 起動待ち
            for (int i = 0; i < 20; i++)
            {
                proc = procField.GetValue(sidecar) as Process;
                if (proc != null) break;
                await Task.Delay(100);
            }

            Assert.NotNull(proc);
            int pid = proc.Id;
            Assert.False(proc.HasExited);

            sidecar.Dispose();
            _sidecars.Remove(sidecar);

            // プロセスが終了したか確認
            bool isGone = false;
            for (int i = 0; i < 20; i++)
            {
                try 
                { 
                    Process.GetProcessById(pid); 
                    await Task.Delay(200); 
                }
                catch (ArgumentException) { isGone = true; break; }
            }
            Assert.True(isGone, "Process should be terminated after sidecar.Dispose()");
        }
    }
}