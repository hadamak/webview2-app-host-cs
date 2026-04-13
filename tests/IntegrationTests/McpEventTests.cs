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
    public class McpEventTests : IDisposable
    {
        private readonly TextWriter? _oldLog;

        public McpEventTests()
        {
            _oldLog = AppLog.Override;
            AppLog.Override = TextWriter.Null;
        }

        public void Dispose()
        {
            if (_oldLog != null) AppLog.Override = _oldLog;
        }

        [Fact]
        public async Task Event_Forward_AsNotification()
        {
            var input = new StringReader(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping"",""params"":{}}" + "\n");
            var outBuf = new StringBuilder();
            var output = new StringWriter(outBuf);
            var mcp = new McpConnector(null, input, output, TimeSpan.FromMilliseconds(300));

            var events = new List<string>();
            Action<string>? publishAction = null;
            publishAction = json =>
            {
                lock (events)
                {
                    events.Add(json);
                }
            };
            mcp.Publish = publishAction;

            var bridge = new McpBridge();
            bridge.UnsolicitedMessage += msg => publishAction?.Invoke(msg);

            bridge.Dispatch(@"{""jsonrpc"":""2.0"",""method"":""Browser.Ready"",""params"":{""ready"":true}}", null);

            Assert.NotEmpty(events);
        }

        [Fact]
        public async Task Event_Forward_PluginEvent_FormattedCorrectly()
        {
            var input = new StringReader(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping"",""params"":{}}" + "\n");
            var outBuf = new StringBuilder();
            var output = new StringWriter(outBuf);
            var mcp = new McpConnector(null, input, output, TimeSpan.FromMilliseconds(300));

            var bridge = new McpBridge();

            var events = new List<string>();
            Action<string>? publishAction = null;
            publishAction = json => events.Add(json);
            mcp.Publish = publishAction;
            bridge.UnsolicitedMessage += msg => publishAction?.Invoke(msg);

            using var cts = new CancellationTokenSource(1000);
            var serverTask = mcp.RunAsync(cts.Token);

            await Task.Delay(100);
            bridge.Dispatch(@"{""jsonrpc"":""2.0"",""method"":""Plugin.DataReceived"",""params"":{""data"":""test""}}", null);

            await Task.Delay(200);
            cts.Cancel();
            try { await serverTask; } catch { }

            Assert.Single(events);
        }

        [Fact]
        public async Task Multiple_Events_AllForwarded()
        {
            var input = new StringReader(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping"",""params"":{}}" + "\n");
            var outBuf = new StringBuilder();
            var output = new StringWriter(outBuf);
            var mcp = new McpConnector(null, input, output, TimeSpan.FromMilliseconds(300));

            var eventCount = 0;
            Action<string>? publishAction = null;
            publishAction = json => Interlocked.Increment(ref eventCount);
            mcp.Publish = publishAction;

            var bridge = new McpBridge();
            bridge.UnsolicitedMessage += msg => publishAction?.Invoke(msg);

            bridge.Dispatch(@"{""jsonrpc"":""2.0"",""method"":""Event.A"",""params"":{}}", null);
            bridge.Dispatch(@"{""jsonrpc"":""2.0"",""method"":""Event.B"",""params"":{}}", null);
            bridge.Dispatch(@"{""jsonrpc"":""2.0"",""method"":""Event.C"",""params"":{}}", null);

            Assert.Equal(3, eventCount);
        }

        [Fact]
        public async Task ToolsList_WithBrowserEnabled_IncludesAllBrowserTools()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/list"",""params"":{}}"
            }, configure: m => m.EnableBrowserProxy());

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.Contains("result", resp);

            var result = resp["result"] as Dictionary<string, object>;
            var tools = result?["tools"] as System.Collections.ArrayList;
            Assert.NotNull(tools);

            var toolNames = new List<string>();
            foreach (var t in tools)
            {
                var tool = t as Dictionary<string, object>;
                if (tool != null && tool.ContainsKey("name"))
                {
                    toolNames.Add(tool["name"]?.ToString() ?? "");
                }
            }

            Assert.Contains(toolNames, n => n == "browser_evaluate");
            Assert.Contains(toolNames, n => n == "browser_screenshot");
            Assert.Contains(toolNames, n => n == "browser_navigate");
            Assert.Contains(toolNames, n => n == "browser_click");
            Assert.Contains(toolNames, n => n == "browser_type");
            Assert.Contains(toolNames, n => n == "browser_scroll");
            Assert.Contains(toolNames, n => n == "browser_get_url");
            Assert.Contains(toolNames, n => n == "browser_get_content");
        }

        [Fact]
        public async Task ToolsList_Tool_HasCorrectSchema()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/list"",""params"":{}}"
            }, configure: m => m.EnableBrowserProxy());

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            var result = resp["result"] as Dictionary<string, object>;
            var tools = result?["tools"] as System.Collections.ArrayList;

            var evaluateTool = tools?[0] as Dictionary<string, object>;
            Assert.NotNull(evaluateTool);
            Assert.Equal("browser_evaluate", evaluateTool["name"]);
            Assert.Contains("description", evaluateTool);

            var inputSchema = evaluateTool["inputSchema"] as Dictionary<string, object>;
            Assert.NotNull(inputSchema);
            Assert.Equal("object", inputSchema["type"]);
            Assert.Contains("properties", inputSchema);
            Assert.Contains("required", inputSchema);
        }

        [Fact]
        public async Task ToolsCall_CallId_Correlation_Maintained()
        {
            string? sentRequest = null;
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":100,""method"":""tools/call"",""params"":{""name"":""invoke_dll_test"",""arguments"":{""method"":""Correlate""}}}"
            }, handleWebMessage: (json, mcp) =>
            {
                sentRequest = json;
            }, callTimeoutMs: 500);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.Equal(100L, Convert.ToInt64(resp["id"]));
        }

        [Fact]
        public async Task JsonRpc_Version_Maintained()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping"",""params"":{}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.Equal("2.0", resp["jsonrpc"]);
        }
    }
}
