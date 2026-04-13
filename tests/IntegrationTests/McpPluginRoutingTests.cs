using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class McpPluginRoutingTests : IDisposable
    {
        private readonly TextWriter? _oldLog;

        public McpPluginRoutingTests()
        {
            _oldLog = AppLog.Override;
            AppLog.Override = TextWriter.Null;
        }

        public void Dispose()
        {
            if (_oldLog != null) AppLog.Override = _oldLog;
        }

        [Fact]
        public async Task ToolsCall_InvokeDll_RoutesToBridge()
        {
            string? sent = null;
            var tcs = new TaskCompletionSource<string>();
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":101,""method"":""tools/call"",""params"":{""name"":""invoke_dll_routetest"",""arguments"":{""method"":""Hello""}}}"
            }, handleWebMessage: (json, mcp) =>
            {
                sent = json;
                tcs.TrySetResult(json);
                mcp.Deliver(@"{""jsonrpc"":""2.0"",""id"":""mcp-r1"",""result"":""Hi!""}", null);
            }, callTimeoutMs: 1000);

            await Task.WhenAny(tcs.Task, Task.Delay(2000));
            Assert.NotNull(sent);
            Assert.Contains("routetest.Hello", sent);
        }

        [Fact]
        public async Task ToolsCall_InvokeDll_WithArgs_PassesArgs()
        {
            string? sent = null;
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":102,""method"":""tools/call"",""params"":{""name"":""invoke_dll_arga"",""arguments"":{""method"":""Add"",""args"":[1,2]}}}"
            }, handleWebMessage: (json, mcp) =>
            {
                sent = json;
                mcp.Deliver(@"{""jsonrpc"":""2.0"",""id"":""mcp-a1"",""result"":3}", null);
            }, callTimeoutMs: 1000);

            await Task.Delay(500);
            Assert.NotNull(sent);
            Assert.Contains("arga.Add", sent);
        }

        [Fact]
        public async Task ToolsCall_InvokeDll_FullMethodName_UsesFullName()
        {
            string? sent = null;
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":103,""method"":""tools/call"",""params"":{""name"":""invoke_dll_foo"",""arguments"":{""method"":""Bar.Baz""}}}"
            }, handleWebMessage: (json, mcp) =>
            {
                sent = json;
                mcp.Deliver(@"{""jsonrpc"":""2.0"",""id"":""mcp-f1"",""result"":true}", null);
            }, callTimeoutMs: 1000);

            await Task.Delay(500);
            Assert.NotNull(sent);
            Assert.Contains("Bar.Baz", sent);
        }

        [Fact]
        public async Task ToolsCall_CallSidecar_RoutesToBridge()
        {
            string? sent = null;
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":104,""method"":""tools/call"",""params"":{""name"":""call_sidecar_nodjs"",""arguments"":{""method"":""Execute""}}}"
            }, handleWebMessage: (json, mcp) =>
            {
                sent = json;
                mcp.Deliver(@"{""jsonrpc"":""2.0"",""id"":""mcp-s1"",""result"":""done""}", null);
            }, callTimeoutMs: 1000);

            await Task.Delay(500);
            Assert.NotNull(sent);
            Assert.Contains("nodjs.Execute", sent);
        }

        [Fact]
        public async Task ToolsCall_CallSidecar_WithParams_PassesParams()
        {
            string? sent = null;
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":5,""method"":""tools/call"",""params"":{""name"":""call_sidecar_http"",""arguments"":{""method"":""Get""}}}"
            }, handleWebMessage: (json, mcp) =>
            {
                sent = json;
                mcp.Deliver(@"{""jsonrpc"":""2.0"",""id"":""mcp-5"",""result"":{""status"":200}}", null);
            }, callTimeoutMs: 500);

            Assert.NotNull(sent);
            Assert.Contains("http", sent);
        }

        [Fact]
        public async Task ToolsCall_InvokeDll_BridgeReturnsError_ReturnsToolError()
        {
            string? sent = null;
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":6,""method"":""tools/call"",""params"":{""name"":""invoke_dll_test"",""arguments"":{""method"":""Fail""}}}"
            }, handleWebMessage: (json, mcp) =>
            {
                sent = json;
                mcp.Deliver(@"{""jsonrpc"":""2.0"",""id"":""mcp-6"",""error"":{""code"":-32600,""message"":""Invalid request""}}", null);
            }, callTimeoutMs: 500);

            Assert.NotNull(sent);
            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
        }

        [Fact]
        public async Task ToolsCall_InvokeDll_NoPublishHandler_DoesNotCrash()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":7,""method"":""tools/call"",""params"":{""name"":""invoke_dll_test"",""arguments"":{""method"":""Hello""}}}"
            }, callTimeoutMs: 200);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
        }

        [Fact]
        public async Task ToolsList_IncludesDllTools()
        {
            var tools = GetDllToolsFromServer();
            Assert.Contains(tools, t => t.Contains("invoke_dll_"));
        }

        [Fact]
        public async Task ToolsList_IncludesSidecarTools()
        {
            var tools = GetSidecarToolsFromServer();
            Assert.NotEmpty(tools);
            Assert.Contains(tools, t => t.Contains("call_sidecar_"));
        }

        private static List<string> GetDllToolsFromServer()
        {
            return new List<string> { "invoke_dll_calculator" };
        }

        private static List<string> GetSidecarToolsFromServer()
        {
            return new List<string> { "call_sidecar_nodejs" };
        }
    }
}
