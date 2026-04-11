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

namespace HostTests
{
    public class SidecarTests
    {
        private static readonly JavaScriptSerializer s_json = new JavaScriptSerializer();

        private static bool IsNodeAvailable()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("node", "--version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit(1000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        [Fact]
        public async Task TestStreamingModeAsync()
        {
            if (!IsNodeAvailable()) return;

            var script = "process.stdin.on('data', d => process.stdout.write(d));";
            var scriptPath = Path.GetTempFileName();
            File.WriteAllText(scriptPath, script);

            try
            {
                var entry = new SidecarEntry
                {
                    Alias = "EchoStream",
                    Executable = "node",
                    Args = new[] { scriptPath },
                    Mode = "streaming"
                };

                using var cts = new CancellationTokenSource(5000);
                var sidecar = new SidecarConnector(entry, cts.Token);
                
                var received = new TaskCompletionSource<string>();
                sidecar.Publish = json => received.TrySetResult(json);

                sidecar.Start();
                
                var testMsg = "{\"test\":123}";
                sidecar.Deliver(testMsg, null);

                var result = await received.Task;
                Assert.Equal(testMsg, result.Trim());

                sidecar.Dispose();
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        [Fact]
        public async Task TestRestartOnFailureAsync()
        {
            if (!IsNodeAvailable()) return;

            // 1回だけ落ちて、次は普通に動くスクリプト
            var markerFile = Path.Combine(Path.GetTempPath(), "sidecar_restart_marker_" + Guid.NewGuid().ToString("N"));
            var script = $@"
                const fs = require('fs');
                if (!fs.existsSync('{markerFile.Replace("\\", "\\\\")}')) {{
                    fs.writeFileSync('{markerFile.Replace("\\", "\\\\")}', 'done');
                    process.exit(1);
                }}
                process.stdin.on('data', d => process.stdout.write(d));
            ";
            var scriptPath = Path.GetTempFileName();
            File.WriteAllText(scriptPath, script);

            try
            {
                var entry = new SidecarEntry
                {
                    Alias = "Restarter",
                    Executable = "node",
                    Args = new[] { scriptPath },
                    Mode = "streaming"
                };

                using var cts = new CancellationTokenSource(10000);
                var sidecar = new SidecarConnector(entry, cts.Token);
                
                var received = new TaskCompletionSource<string>();
                sidecar.Publish = json => {
                    if (json.Contains("after-restart")) received.TrySetResult(json);
                };

                sidecar.Start();
                
                // 1回目 (失敗と再起動) -> 少し待つ
                await Task.Delay(2000);

                var testMsg = "{\"test\":\"after-restart\"}";
                sidecar.Deliver(testMsg, null);

                var result = await received.Task;
                Assert.Contains("after-restart", result);

                sidecar.Dispose();
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
                if (File.Exists(markerFile)) File.Delete(markerFile);
            }
        }

        [Fact]
        public async Task TestRestartLimitAsync()
        {
            if (!IsNodeAvailable()) return;

            // 即座に死ぬスクリプト
            var entry = new SidecarEntry
            {
                Alias = "Suicide",
                Executable = "node",
                Args = new[] { "-e", "process.exit(1)" },
                Mode = "streaming"
            };

            using var cts = new CancellationTokenSource(10000);
            var sidecar = new SidecarConnector(entry, cts.Token);
            
            sidecar.Start();
            
            // 指数バックオフがあるので 5 回の再試行には数秒かかる (1+2+4+8+16 = 31s? No, Math.Pow(2, attempt) delay)
            // attempt 0: 1s, 1: 2s, 2: 4s...
            // とりあえず少し待って、再起動回数が増えていることを確認
            await Task.Delay(4000);

            var field = typeof(SidecarConnector).GetField("_restartCount", BindingFlags.NonPublic | BindingFlags.Instance);
            var count = (int)(field?.GetValue(sidecar) ?? 0);
            
            Assert.True(count >= 1, "Restart count should be at least 1");
            
            sidecar.Dispose();
        }

        [Fact]
        public void TestSidecar_IsForMe_ParsesCorrectly()
        {
            var entry = new SidecarEntry { Alias = "Test" };
            var sidecar = new SidecarConnector(entry, CancellationToken.None);
            var method = typeof(SidecarConnector).GetMethod("IsForMe", BindingFlags.NonPublic | BindingFlags.Instance);
            
            Assert.True((bool)method!.Invoke(sidecar, new object[] { "{\"jsonrpc\":\"2.0\",\"method\":\"Test.Do\"}", null! })!);
            Assert.True((bool)method!.Invoke(sidecar, new object[] { "{\"jsonrpc\":\"2.0\",\"method\":\"test.Do\"}", null! })!);
            Assert.False((bool)method!.Invoke(sidecar, new object[] { "{\"jsonrpc\":\"2.0\",\"method\":\"Other.Do\"}", null! })!);
            Assert.False((bool)method!.Invoke(sidecar, new object[] { "{\"method\":\"Test.Do\"}", null! })!); // Missing jsonrpc 2.0
        }

        [Fact]
        public async Task TestSidecar_Lifecycle_StartStop()
        {
            if (!IsNodeAvailable()) return;

            var entry = new SidecarEntry { Alias = "Lifecycle", Executable = "node", Args = new[] { "-e", "setInterval(()=>{}, 1000)" }, Mode = "streaming" };
            var sidecar = new SidecarConnector(entry, CancellationToken.None);
            
            sidecar.Start();
            await Task.Delay(500);
            
            var field = typeof(SidecarConnector).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance);
            var proc = field?.GetValue(sidecar) as System.Diagnostics.Process;
            
            Assert.NotNull(proc);
            Assert.False(proc!.HasExited);
            
            sidecar.Dispose();
            // Wait a bit for process to exit after Kill
            Assert.True(proc.WaitForExit(2000));
        }
    }
}
