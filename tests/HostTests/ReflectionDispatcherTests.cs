using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class ReflectionDispatcherTests : IDisposable
    {
        private readonly System.IO.TextWriter _oldOverride;

        public ReflectionDispatcherTests()
        {
            _oldOverride = AppLog.Override;
            AppLog.Override = System.IO.TextWriter.Null;
        }

        public void Dispose()
        {
            AppLog.Override = _oldOverride;
        }

        private class TestRequest { public string ClassName = ""; public string MethodName = ""; }
        private class TestNotification { public string EventName = ""; }

        private class TestJsonRpcDispatcher : ReflectionDispatcherBase
        {
            public TestRequest? LastRequest;
            public TestNotification? LastNotification;
            public ManualResetEventSlim DispatchEvent = new ManualResetEventSlim(false);

            public TestJsonRpcDispatcher()
            {
                this._postMessage = (json) => 
                {
                    DispatchEvent.Set();
                };
            }

            protected override string SourceName => "Test";
            protected override bool ShouldWrapAsHandle(object result) => false;

            protected override Task<object?> ResolveTypeAsync(
                string? source, Dictionary<string, object>? p, string className, string methodName,
                object?[] argsRaw, object? id)
            {
                LastRequest = new TestRequest { ClassName = className, MethodName = methodName };
                DispatchEvent.Set(); // Setting here to ensure we unblock if SendJsonRpcResult is skipped, though it usually isn't.
                return Task.FromResult<object?>(null);
            }

            protected override void OnNotificationReceived(string eventName, object? eventParams)
            {
                LastNotification = new TestNotification { EventName = eventName };
                DispatchEvent.Set();
            }

            public string SerializeResponse(object? result, object? id, string? error)
            {
                // This is a test convenience
                var dict = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id };
                if (error != null) dict["error"] = new Dictionary<string, object> { ["code"] = -32000, ["message"] = error };
                else dict["result"] = result;
                return s_json.Serialize(dict);
            }

            public void HandleWebMessageCoreSync(string json, Dictionary<string, object>? dict)
            {
                DispatchEvent.Reset();
                base.HandleWebMessageCore(json, dict);
                
                // Wait for async dispatch to complete instead of Thread.Sleep
                DispatchEvent.Wait(TimeSpan.FromSeconds(2));
            }
        }

        [Fact]
        public void HandleWebMessageCoreSync_WithValidRequest_ParsesClassAndMethodName()
        {
            var dispatcher = new TestJsonRpcDispatcher();
            var testMsg = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Test.StaticClass.StaticMethod\",\"params\":[\"arg1\",42]}";
            
            dispatcher.HandleWebMessageCoreSync(testMsg, null);
            
            Assert.NotNull(dispatcher.LastRequest);
            Assert.Equal("StaticClass", dispatcher.LastRequest!.ClassName);
            Assert.Equal("StaticMethod", dispatcher.LastRequest.MethodName);
        }

        [Fact]
        public void HandleWebMessageCoreSync_WithEmptyParams_ParsesSuccessfully()
        {
            var dispatcher = new TestJsonRpcDispatcher();
            dispatcher.HandleWebMessageCoreSync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"Test.StaticClass.MethodNoParams\",\"params\":[]}", null);
            
            Assert.NotNull(dispatcher.LastRequest);
        }

        [Fact]
        public void HandleWebMessageCoreSync_WithWrongJsonRpcVersion_IgnoresRequest()
        {
            var dispatcher = new TestJsonRpcDispatcher();
            dispatcher.HandleWebMessageCoreSync("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"Test.Class.Method\",\"params\":[]}", null);
            
            Assert.Null(dispatcher.LastRequest);
        }

        [Fact]
        public void SerializeResponse_WithValidResult_IncludesRequiredJsonRpcProperties()
        {
            var dispatcher = new TestJsonRpcDispatcher();
            var json = dispatcher.SerializeResponse(42, 1, null);
            
            Assert.Contains("\"jsonrpc\":\"2.0\"", json);
            Assert.Contains("\"id\":1", json);
            Assert.Contains("\"result\":42", json);
        }

        [Fact]
        public void HandleWebMessageCoreSync_WithEventNotification_TriggersNotificationReceived()
        {
            var dispatcher = new TestJsonRpcDispatcher();
            dispatcher.HandleWebMessageCoreSync("{\"jsonrpc\":\"2.0\",\"method\":\"Test.OnEvent\",\"params\":{\"key\":\"value\"}}", null);
            
            Assert.NotNull(dispatcher.LastNotification);
            Assert.Equal("OnEvent", dispatcher.LastNotification!.EventName);
        }
    }
}
