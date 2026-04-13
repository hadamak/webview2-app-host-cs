using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class McpBrowserToolTests : IDisposable
    {
        private readonly TextWriter? _oldLog;
        private readonly MockBrowserTools _mockBrowser;

        public McpBrowserToolTests()
        {
            _oldLog = AppLog.Override;
            AppLog.Override = TextWriter.Null;
            _mockBrowser = new MockBrowserTools();
        }

        public void Dispose()
        {
            if (_oldLog != null) AppLog.Override = _oldLog;
        }

        [Fact]
        public async Task ToolsCall_BrowserEvaluate_ReturnsResult()
        {
            _mockBrowser.EvaluateReturnValue = "\"hello world\"";

            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""browser_evaluate"",""arguments"":{""script"":""'hello world'""}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.False(McpTestHelper.IsErrorResponse(resp));
            var text = McpTestHelper.ExtractToolTextContent(resp);
            Assert.Contains("hello world", text);
            Assert.Single(_mockBrowser.EvaluateCalls);
            Assert.Equal("'hello world'", _mockBrowser.EvaluateCalls[0].Script);
        }

        [Fact]
        public async Task ToolsCall_BrowserEvaluate_WithoutScript_ReturnsError()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/call"",""params"":{""name"":""browser_evaluate"",""arguments"":{}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
            var error = McpTestHelper.GetErrorMessage(resp);
            Assert.Contains("script", error);
        }

        [Fact]
        public async Task ToolsCall_BrowserNavigate_ReturnsSuccess()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":3,""method"":""tools/call"",""params"":{""name"":""browser_navigate"",""arguments"":{""url"":""https://example.com""}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.False(McpTestHelper.IsErrorResponse(resp));
            Assert.Single(_mockBrowser.NavigateCalls);
            Assert.Equal("https://example.com", _mockBrowser.NavigateCalls[0].Url);
        }

        [Fact]
        public async Task ToolsCall_BrowserClick_ReturnsSuccess()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""browser_click"",""arguments"":{""selector"":""#submit""}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.False(McpTestHelper.IsErrorResponse(resp));
            Assert.Single(_mockBrowser.ClickCalls);
            Assert.Equal("#submit", _mockBrowser.ClickCalls[0].Selector);
        }

        [Fact]
        public async Task ToolsCall_BrowserType_ReturnsSuccess()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":5,""method"":""tools/call"",""params"":{""name"":""browser_type"",""arguments"":{""selector"":""input[name=q]"",""text"":""search query""}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.False(McpTestHelper.IsErrorResponse(resp));
            Assert.Single(_mockBrowser.TypeCalls);
            Assert.Equal("input[name=q]", _mockBrowser.TypeCalls[0].Selector);
            Assert.Equal("search query", _mockBrowser.TypeCalls[0].Text);
        }

        [Fact]
        public async Task ToolsCall_BrowserScroll_ReturnsSuccess()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":6,""method"":""tools/call"",""params"":{""name"":""browser_scroll"",""arguments"":{""x"":100,""y"":200}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.False(McpTestHelper.IsErrorResponse(resp));
            Assert.Single(_mockBrowser.ScrollCalls);
            Assert.Equal(100, _mockBrowser.ScrollCalls[0].X);
            Assert.Equal(200, _mockBrowser.ScrollCalls[0].Y);
        }

        [Fact]
        public async Task ToolsCall_BrowserGetUrl_ReturnsUrl()
        {
            _mockBrowser.GetUrlReturnValue = "https://test.example.com/page";

            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":7,""method"":""tools/call"",""params"":{""name"":""browser_get_url"",""arguments"":{}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.False(McpTestHelper.IsErrorResponse(resp));
            var text = McpTestHelper.ExtractToolTextContent(resp);
            Assert.Contains("https://test.example.com/page", text);
            Assert.Single(_mockBrowser.GetUrlCalls);
        }

        [Fact]
        public async Task ToolsCall_BrowserGetContent_ReturnsHtml()
        {
            _mockBrowser.GetContentReturnValue = "<html><body>Test</body></html>";

            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":8,""method"":""tools/call"",""params"":{""name"":""browser_get_content"",""arguments"":{}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.False(McpTestHelper.IsErrorResponse(resp));
            var text = McpTestHelper.ExtractToolTextContent(resp);
            Assert.Contains("<html>", text);
            Assert.Contains("Test", text);
            Assert.Single(_mockBrowser.GetContentCalls);
        }

        [Fact]
        public async Task ToolsCall_BrowserScreenshot_ReturnsImageData()
        {
            _mockBrowser.ScreenshotReturnValue = ("abc123BASE64", 1920, 1080);

            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":9,""method"":""tools/call"",""params"":{""name"":""browser_screenshot"",""arguments"":{}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.False(McpTestHelper.IsErrorResponse(resp));

            var result = resp["result"] as Dictionary<string, object>
                ?? throw new Exception("No result");
            var content = result["content"] as System.Collections.ArrayList
                ?? throw new Exception("No content");

            var imageContent = content[0] as Dictionary<string, object>;
            Assert.Equal("image", imageContent?["type"]);
            Assert.Equal("abc123BASE64", imageContent?["data"]);
            Assert.Equal("image/png", imageContent?["mimeType"]);

            var textContent = content[1] as Dictionary<string, object>;
            Assert.Equal("1920x1080px", textContent?["text"]);

            Assert.Single(_mockBrowser.ScreenshotCalls);
        }

        [Fact]
        public async Task ToolsCall_BrowserDisabled_ReturnsError()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":10,""method"":""tools/call"",""params"":{""name"":""browser_evaluate"",""arguments"":{""script"":""1""}}}"
            });

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
            var error = McpTestHelper.GetErrorMessage(resp);
            Assert.Contains("無効", error);
        }

        [Fact]
        public async Task ToolsCall_UnknownBrowserTool_ReturnsError()
        {
            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":11,""method"":""tools/call"",""params"":{""name"":""browser_unknown"",""arguments"":{}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
        }

        [Fact]
        public async Task BrowserTools_Evaluate_ThrowsException_ReturnsError()
        {
            _mockBrowser.EvaluateImpl = (s, ct) => throw new Exception("JS Error: undefined variable");

            var lines = await McpTestHelper.RunServerAsync(new[]
            {
                @"{""jsonrpc"":""2.0"",""id"":12,""method"":""tools/call"",""params"":{""name"":""browser_evaluate"",""arguments"":{""script"":""undefinedVar""}}}"
            }, mockBrowser: _mockBrowser);

            Assert.NotEmpty(lines);
            var resp = McpTestHelper.ParseJson(lines[0]);
            Assert.True(McpTestHelper.IsErrorResponse(resp));
            var error = McpTestHelper.GetErrorMessage(resp);
            Assert.Contains("undefined variable", error);
        }
    }
}
