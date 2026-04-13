using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class McpErrorHandlingTests : IDisposable
    {
        private readonly TextWriter? _oldLog;

        public McpErrorHandlingTests()
        {
            _oldLog = AppLog.Override;
            AppLog.Override = TextWriter.Null;
        }

        public void Dispose()
        {
            if (_oldLog != null) AppLog.Override = _oldLog;
        }

        private async Task<List<string>> RunServerRawAsync(string[] inputLines, int timeoutMs = 2000)
        {
            var input = new StringReader(string.Join("\n", inputLines) + "\n");
            var outBuf = new StringBuilder();
            var output = new StringWriter(outBuf);
            var mcp = new McpConnector(null, input, output, TimeSpan.FromMilliseconds(300));

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

        [Fact]
        public async Task InvalidJson_ReturnsParseError()
        {
            var lines = await RunServerRawAsync(new[]
            {
                "{invalid json"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.Contains("error", resp);
            var error = resp["error"] as Dictionary<string, object>;
            Assert.NotNull(error);
            Assert.Equal(-32700, error["code"]);
        }

        [Fact]
        public async Task EmptyJson_ReturnsParseError()
        {
            var lines = await RunServerRawAsync(new[]
            {
                "{invalid}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.Contains("error", resp);
            var error = resp["error"] as Dictionary<string, object>;
            Assert.Equal(-32700, error?["code"]);
        }

        [Fact]
        public async Task NullJson_ReturnsNoResponse()
        {
            var lines = await RunServerRawAsync(new[]
            {
                "null"
            });

            Assert.Empty(lines);
        }

        [Fact]
        public async Task UnknownMethod_ReturnsMethodNotFoundError()
        {
            var lines = await RunServerRawAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""unknown_method"",""params"":{}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.Contains("error", resp);
            var error = resp["error"] as Dictionary<string, object>;
            Assert.Equal(-32601, error?["code"]);
        }

        [Fact]
        public async Task UnknownMethod_NoId_SkipsResponse()
        {
            var lines = await RunServerRawAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""method"":""unknown_method"",""params"":{}}"
            });

            Assert.Empty(lines);
        }

        [Fact]
        public async Task ToolsCall_NoName_ReturnsError()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/call"",""params"":{}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
            var error = McpTestHelper.GetErrorMessage(resp);
            Assert.Contains("ツール名が指定されていません", error);
        }

        [Fact]
        public async Task ToolsCall_UnknownTool_ReturnsError()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":3,""method"":""tools/call"",""params"":{""name"":""unknown_tool"",""arguments"":{}}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
            var error = McpTestHelper.GetErrorMessage(resp);
            Assert.Contains("未知のツール", error);
        }

        [Fact]
        public async Task ToolsCall_DllNoMethod_ReturnsError()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""invoke_dll_test"",""arguments"":{}}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
            var error = McpTestHelper.GetErrorMessage(resp);
            Assert.Contains("method", error);
        }

        [Fact]
        public async Task ToolsCall_SidecarNoMethod_ReturnsError()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":5,""method"":""tools/call"",""params"":{""name"":""call_sidecar_test"",""arguments"":{}}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
            var error = McpTestHelper.GetErrorMessage(resp);
            Assert.Contains("method", error);
        }

        [Fact]
        public async Task Ping_ReturnsSuccess()
        {
            var lines = await RunServerRawAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":6,""method"":""ping"",""params"":{}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.Contains("result", resp);
            Assert.Equal(6L, Convert.ToInt64(resp["id"]));
        }

        [Fact]
        public async Task Initialize_WithCapabilities_ReturnsServerInfo()
        {
            var lines = await RunServerRawAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":7,""method"":""initialize"",""params"":{""protocolVersion"":""2024-11-05"",""capabilities"":{},""clientInfo"":{""name"":""test-client"",""version"":""1.0.0""}}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.Contains("result", resp);
            var result = resp["result"] as Dictionary<string, object>;
            Assert.Equal("2024-11-05", result?["protocolVersion"]);
            Assert.Contains("capabilities", result);
            Assert.Contains("serverInfo", result);
            var serverInfo = result?["serverInfo"] as Dictionary<string, object>;
            Assert.Contains("name", serverInfo);
            Assert.Contains("version", serverInfo);
        }

        [Fact]
        public async Task Initialize_MultipleRequests_ProcessesInOrder()
        {
            var lines = await RunServerRawAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2024-11-05"",""capabilities"":{},""clientInfo"":{""name"":""a"",""version"":""1""}}}",
                @"{""jsonrpc"":""2.0"",""id"":2,""method"":""ping"",""params"":{}}",
                @"{""jsonrpc"":""2.0"",""id"":3,""method"":""ping"",""params"":{}}"
            }, timeoutMs: 3000);

            Assert.True(lines.Count >= 2, $"Expected at least 2 responses, got {lines.Count}");
            Assert.Contains("result", McpTestHelper.ParseJson(lines[0]));
        }

        [Fact]
        public async Task WhitespaceOnlyLine_Skipped()
        {
            var lines = await RunServerRawAsync(new[]
            {
                "   ",
                @"{""jsonrpc"":""2.0"",""id"":8,""method"":""ping"",""params"":{}}",
                ""
            });

            Assert.Single(lines);
            Assert.Equal(8L, Convert.ToInt64(McpTestHelper.ParseJson(lines[0])["id"]));
        }

        [Fact]
        public async Task ToolsCall_InvokeDll_Timeout_ReturnsError()
        {
            string? sent = null;
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":9,""method"":""tools/call"",""params"":{""name"":""invoke_dll_slow"",""arguments"":{""method"":""SlowMethod""}}}"
            }, handleWebMessage: (json, mcp) =>
            {
                sent = json;
            }, callTimeoutMs: 100);

            await Task.Delay(200);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
            var error = McpTestHelper.GetErrorMessage(resp);
            Assert.NotNull(error);
        }
    }
}
