using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WebView2AppHost;

namespace HostTests
{
    internal static class SidecarTests
    {
        private static readonly JavaScriptSerializer s_json = new JavaScriptSerializer();

        public static void RunAll()
        {
            Console.WriteLine("\n--- Sidecar Integration Tests ---");
            
            if (!IsNodeAvailable())
            {
                Console.WriteLine("  [SKIP] Node.js is not available in PATH.");
                return;
            }

            try
            {
                TestStreamingModeAsync().GetAwaiter().GetResult();
                Console.WriteLine("  Sidecar streaming mode test passed.");
            }
            catch (Exception ex)
            {
                throw new Exception("Sidecar streaming test failed", ex);
            }
        }

        private static bool IsNodeAvailable()
        {
            try {
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "node", Arguments = "-v", UseShellExecute = false, 
                    CreateNoWindow = true, RedirectStandardOutput = true })) {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            } catch { return false; }
        }

        private static async Task TestStreamingModeAsync()
        {
            var testSidecarPath = "";
            var searchDir = new DirectoryInfo(Directory.GetCurrentDirectory());
            
            for (int i = 0; i < 5; i++)
            {
                var candidate = Path.Combine(searchDir.FullName, "tests", "TestSidecar", "test_sidecar.js");
                if (File.Exists(candidate))
                {
                    testSidecarPath = candidate;
                    break;
                }
                searchDir = searchDir.Parent;
                if (searchDir == null) break;
            }

            if (string.IsNullOrEmpty(testSidecarPath))
                throw new FileNotFoundException("Could not find test_sidecar.js");

            var entry = new SidecarEntry
            {
                Alias = "TestSidecar",
                Mode = "streaming",
                Executable = "node",
                WorkingDirectory = Path.GetDirectoryName(testSidecarPath),
                Args = new[] { testSidecarPath },
                WaitForReady = true
            };

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using (var connector = new SidecarConnector(entry, cts.Token))
            {
                string lastResponse = null;
                var responseEvent = new ManualResetEventSlim(false);

                connector.Publish = json => {
                    Console.WriteLine("  [Test] Received from Sidecar: " + json);
                    lastResponse = json;
                    responseEvent.Set();
                };

                Console.WriteLine("  [Test] Starting Sidecar...");
                connector.Start();

                // Math.Add
                var request = new Dictionary<string, object> {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "t1",
                    ["method"] = "TestSidecar.Test.Math.Add", // method prefix を alias に合わせる
                    ["params"] = new[] { 10, 20 }
                };
                
                var reqJson = s_json.Serialize(request);
                Console.WriteLine("  [Test] Sending Request: " + reqJson);
                connector.Deliver(reqJson);

                if (!responseEvent.Wait(5000))
                    throw new TimeoutException("Sidecar did not respond to Math.Add");

                var resp = s_json.Deserialize<Dictionary<string, object>>(lastResponse);
                Assert(resp["id"].ToString() == "t1", "Response ID mismatch");
                Assert(Convert.ToInt32(resp["result"]) == 30, "Math.Add result mismatch");

                // Error.Throw
                responseEvent.Reset();
                var errRequest = new Dictionary<string, object> {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "t2",
                    ["method"] = "TestSidecar.Test.Error.Throw",
                    ["params"] = new object[] { }
                };

                Console.WriteLine("  [Test] Sending Error Request: " + s_json.Serialize(errRequest));
                connector.Deliver(s_json.Serialize(errRequest));

                if (!responseEvent.Wait(5000))
                    throw new TimeoutException("Sidecar did not respond to Error.Throw");

                var errResp = s_json.Deserialize<Dictionary<string, object>>(lastResponse);
                Assert(errResp.ContainsKey("error"), "Response should contain error");

                // Restart after child process exits
                responseEvent.Reset();
                lastResponse = null;
                var exitRequest = new Dictionary<string, object> {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "t3",
                    ["method"] = "TestSidecar.Test.Process.Exit",
                    ["params"] = new object[] { }
                };

                Console.WriteLine("  [Test] Sending Exit Request: " + s_json.Serialize(exitRequest));
                connector.Deliver(s_json.Serialize(exitRequest));
                Thread.Sleep(2500);

                var restartRequest = new Dictionary<string, object> {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "t4",
                    ["method"] = "TestSidecar.Test.Math.Add",
                    ["params"] = new[] { 7, 8 }
                };

                Console.WriteLine("  [Test] Sending Restart Verification Request: " + s_json.Serialize(restartRequest));
                connector.Deliver(s_json.Serialize(restartRequest));

                if (!responseEvent.Wait(5000))
                    throw new TimeoutException("Sidecar did not restart after process exit");

                var restartResp = s_json.Deserialize<Dictionary<string, object>>(lastResponse);
                Assert(restartResp["id"].ToString() == "t4", "Restart response ID mismatch");
                Assert(Convert.ToInt32(restartResp["result"]) == 15, "Restarted sidecar result mismatch");
            }
        }

        private static void Assert(bool cond, string label)
        {
            if (!cond) throw new Exception("FAILED: " + label);
        }
    }
}
