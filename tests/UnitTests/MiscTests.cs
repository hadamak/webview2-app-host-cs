using System;
using System.IO;
using System.Text;
using Xunit;
using WebView2AppHost;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HostTests
{
    public class MiscTests
    {
        [Fact]
        public void ParseRange_WithValidRange_ReturnsStartAndEnd()
        {
            var r = WebResourceHandler.ParseRange("bytes=0-499", 1000);
            Assert.NotNull(r);
            Assert.Equal(0, r!.Value.start);
            Assert.Equal(499, r.Value.end);
        }

        [Fact]
        public void ParseRange_WithZeroTotal_ReturnsNull()
        {
            var r = WebResourceHandler.ParseRange("bytes=0-0", 0);
            Assert.Null(r);
        }

        [Fact]
        public void ParseRange_EdgeCases_HandlesCorrectly()
        {
            // Clamping end to total-1
            var r1 = WebResourceHandler.ParseRange("bytes=0-2000", 1000);
            Assert.Equal(999, r1!.Value.end);

            // Suffix range: last 500 bytes
            var r2 = WebResourceHandler.ParseRange("bytes=-500", 1000);
            Assert.Equal(500, r2!.Value.start);
            Assert.Equal(999, r2.Value.end);

            // Open ended range
            var r3 = WebResourceHandler.ParseRange("bytes=500-", 1000);
            Assert.Equal(500, r3!.Value.start);
            Assert.Equal(999, r3.Value.end);

            // Invalid formats should return null
            Assert.Null(WebResourceHandler.ParseRange("invalid", 1000));
            Assert.Null(WebResourceHandler.ParseRange("bytes=500-200", 1000)); // Reversed
            Assert.Null(WebResourceHandler.ParseRange("bytes=1000-1100", 1000)); // Out of bounds
            Assert.Null(WebResourceHandler.ParseRange("bytes=0-499, 500-999", 1000)); // Multi-range (not supported)
        }

        [Fact]
        public void SubStream_Length_ReturnsCorrectValue()
        {
            using var ms = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
            using var sub = new SubStream(ms, 2, 4);
            Assert.Equal(4, sub.Length);
        }

        [Fact]
        public void FromRequestedFeatures_WithWidth_ParsesWidthProperly()
        {
            var opt = PopupWindowOptions.FromRequestedFeatures(true, 10, 10, true, 100, 100, false, false, false, false, 800, 600);
            Assert.Equal(100, opt.Width);
        }

        [Fact]
        public void FromPath_HtmlExtension_ReturnsHtmlMimeType()
        {
            Assert.Equal("text/html; charset=utf-8", MimeTypes.FromPath("index.html"));
        }

        [Fact]
        public void FromPath_UpperCaseExtension_IsCaseInsensitive()
        {
            Assert.Equal("text/html; charset=utf-8", MimeTypes.FromPath("TEST.HTML"));
        }

        [Fact]
        public void AppLog_Log_WritesMessageToOverrideWriter()
        {
            using var sw = new StringWriter();
            var old = AppLog.Override;
            AppLog.Override = sw;
            
            var level = AppLog.MinimumLevel == AppLog.LogLevel.Warn ? AppLog.LogLevel.Warn : AppLog.LogLevel.Info;
            AppLog.Log(level, "Test", "Msg");
            
            AppLog.Override = old;
            
            var logOutput = sw.ToString();
            Assert.Contains("Msg", logOutput);
        }

        [Fact]
        public void DescribeMessageJson_WithSensitiveData_SanitizesPayload()
        {
            var summary = AppLog.DescribeMessageJson(
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"Host.Storage.Set\",\"params\":{\"key\":\"token\",\"value\":\"secret-value\"},\"result\":{\"content\":\"payload\"}}");

            Assert.Contains("method=Host.Storage.Set", summary);
            Assert.Contains("id=7", summary);
            Assert.Contains("params=object(keys=2)", summary);
            Assert.Contains("result=object(keys=1)", summary);
            Assert.DoesNotContain("secret-value", summary);
            Assert.DoesNotContain("payload", summary);
        }

        [Fact]
        public void AppLog_ShouldWrite_SensitiveDataKind_ReturnsFalseOutsideDebug()
        {
#if DEBUG && !SECURE_OFFLINE
            Assert.True(AppLog.ShouldWrite(AppLog.LogLevel.Debug, AppLog.LogDataKind.Sensitive));
#else
            Assert.False(AppLog.ShouldWrite(AppLog.LogLevel.Debug, AppLog.LogDataKind.Sensitive));
#endif
        }

        [Fact]
        public void AppLog_EnableFileOutput_ReturnsFalseInSecureOfflineBuild()
        {
#if SECURE_OFFLINE
            Assert.False(AppLog.EnableFileOutput);
            Assert.Equal(AppLog.LogLevel.Warn, AppLog.MinimumLevel);
#else
            Assert.True(AppLog.EnableFileOutput);
#endif
        }

        [Fact]
        public void CloseRequestState_FullStateMachine_AllTransitionsSucceed()
        {
            var s = new CloseRequestState();
            
            // Initial -> InProgress
            Assert.False(s.IsClosingConfirmed);
            Assert.False(s.IsClosingInProgress);
            s.BeginHostCloseNavigation();
            Assert.True(s.IsClosingInProgress);
            
            // InProgress -> Cancelled (Initial)
            s.CancelHostCloseNavigation();
            Assert.False(s.IsClosingInProgress);
            
            // InProgress -> Confirmed (Success)
            s.BeginHostCloseNavigation();
            bool success = s.TryCompleteCloseNavigation(true);
            Assert.True(success);
            Assert.True(s.IsClosingConfirmed);
            Assert.False(s.IsClosingInProgress);
            
            // Confirmed state is sticky for TryComplete (should return false if not InProgress)
            Assert.False(s.TryCompleteCloseNavigation(true));
            
            // Reset and test Failure path
            var s2 = new CloseRequestState();
            s2.BeginHostCloseNavigation();
            bool failResult = s2.TryCompleteCloseNavigation(false);
            Assert.False(failResult);
            Assert.False(s2.IsClosingConfirmed);
            Assert.False(s2.IsClosingInProgress);
            
            // Direct Close
            var s3 = new CloseRequestState();
            s3.ConfirmDirectClose();
            Assert.True(s3.IsClosingConfirmed);
        }

        [Fact]
        public void GetJsonPayload_WithStringifiedJson_ExtractsStringSuccessfully()
        {
            var p1 = WebMessageHelper.GetJsonPayload(
                () => "{\"source\":\"test\"}",
                () => "\"{\\\"source\\\":\\\"test\\\"}\""
            );
            Assert.Equal("{\"source\":\"test\"}", p1);
        }

        [Fact]
        public void GetJsonPayload_WithObjectJsonAndWinRtException_ExtractsObjectSuccessfully()
        {
            var p2 = WebMessageHelper.GetJsonPayload(
                () => throw new Exception("WinRT error"),
                () => "{\"source\":\"test\"}"
            );
            Assert.Equal("{\"source\":\"test\"}", p2);
        }

        public class TestPlugin
        {
            public string? LastMessage;
            public void Initialize(string json) { }
            public void HandleWebMessage(string json) => LastMessage = json;
        }

        [Fact]
        public void TestPlugin_HandleWebMessage_ReceivesDeliveredJson()
        {
            var dummy = new TestPlugin();
            var initMethod = dummy.GetType().GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (initMethod != null) initMethod.Invoke(dummy, new object[] { "{}" });
            
            var handleMethod = dummy.GetType().GetMethod("HandleWebMessage", new[] { typeof(string) });
            Assert.NotNull(handleMethod);
            
            handleMethod!.Invoke(dummy, new object[] { "{\"test\":1}" });
            Assert.Equal("{\"test\":1}", dummy.LastMessage);
        }

        [Fact]
        public void App_WrapUserScript_WrapsCorrectly()
        {
            var script = "console.log('test');";
            
            // Case 1: Wildcard * (no wrapping)
            Assert.Equal(script, App.WrapUserScript(script, "*"));
            Assert.Equal(script, App.WrapUserScript(script, ""));

            // Case 2: Specific pattern
            var wrapped = App.WrapUserScript(script, "https://example.com/*");
            Assert.Contains("new RegExp('^https://example\\\\.com/.*$', 'i')", wrapped);
            Assert.Contains(script, wrapped);

            // Case 3: Pattern with single quotes (escaping)
            var wrapped2 = App.WrapUserScript(script, "https://o'reilly.com/*");
            Assert.Contains("new RegExp('^https://o\\'reilly\\\\.com/.*$', 'i')", wrapped2);
        }
    }
}
