using System;
using Xunit;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WebView2AppHost;

namespace HostTests
{
    public class McpTests : IDisposable
    {
        private readonly TextWriter? _oldLog;

        public McpTests()
        {
            _oldLog = AppLog.Override;
            AppLog.Override = TextWriter.Null;
        }

        public void Dispose()
        {
            if (_oldLog != null) AppLog.Override = _oldLog;
        }

        [Fact]
        public async Task McpBridge_Tests()
        {
            var bridge = new McpBridge();
            var sent = (string?)null;
            var request = @"{""jsonrpc"":""2.0"",""id"":""mcp-1"",""method"":""Node.Math.Add"",""params"":[1,2]}";
            var response = @"{""jsonrpc"":""2.0"",""id"":""mcp-1"",""result"":3}";

            var callTask = bridge.CallAsync(request, "mcp-1",
                json => { sent = json; bridge.Dispatch(response, null); },
                TimeSpan.FromSeconds(5));

            var result = await callTask;
            Assert.Equal(request, sent);
            Assert.Equal(response, result);
        }

        private static async Task<List<string>> RunServerAsync(
            string[] inputLines,
            Action<string, McpConnector>? handleWebMessage = null,
            int timeoutMs = 2000,
            Action<McpConnector>? configure = null)
        {
            var input = new StringReader(string.Join("\n", inputLines) + "\n");
            var outBuf = new StringBuilder();
            var output = new StringWriter(outBuf);
            var mcp = new McpConnector(null, input, output, callTimeout: TimeSpan.FromMilliseconds(300));

            configure?.Invoke(mcp);
            mcp.Publish = reqJson => handleWebMessage?.Invoke(reqJson, mcp);

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await mcp.RunAsync(cts.Token);
            }
            catch (OperationCanceledException) { }

            var lines = new List<string>();
            foreach (var line in outBuf.ToString().Split('\n'))
            {
                var l = line.Trim();
                if (!string.IsNullOrEmpty(l)) lines.Add(l);
            }
            return lines;
        }

        private static Dictionary<string, object> ParseJson(string json)
        {
            var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return s.Deserialize<Dictionary<string, object>>(json) ?? throw new Exception("JSON parse failed");
        }

        [Fact]
        public async Task McpServer_Initialize_Test()
        {
            var lines = await RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2024-11-05"",""capabilities"":{},""clientInfo"":{""name"":""Test"",""version"":""1.0""}}}"
            });
            Assert.NotEmpty(lines);
            var resp = ParseJson(lines[0]);
            Assert.True(resp.ContainsKey("result"));
        }

        [Fact]
        public async Task McpServer_ToolsList_Test()
        {
            var lines = await RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/list"",""params"":{}}"
            }, configure: m => m.EnableBrowserProxy());
            Assert.NotEmpty(lines);
            var result = ParseJson(lines[0])["result"] as Dictionary<string, object>;
            var tools = result!["tools"] as System.Collections.ArrayList;
            Assert.NotEmpty(tools!);
        }
    }
}
