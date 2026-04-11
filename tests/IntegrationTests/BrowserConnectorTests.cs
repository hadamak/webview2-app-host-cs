using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class BrowserConnectorTests
    {
        [Fact]
        public void IsForMe_RoutingLogic_WorksCorrectly()
        {
            // Bypass the constructor which requires a WebView2 control
            var connector = FormatterServices.GetUninitializedObject(typeof(BrowserConnector)) as BrowserConnector;
            Assert.NotNull(connector);

            var method = typeof(BrowserConnector).GetMethod("IsForMe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            // Test case: matching source from 'source' field
            string jsonExactSource = "{\"source\": \"Browser\", \"method\": \"SomeMethod\"}";
            var result1 = (bool)method!.Invoke(connector, new object?[] { jsonExactSource, null })!;
            Assert.True(result1);

            // Test case: matching source from 'method' prefix
            string jsonMethodPrefix = "{\"method\": \"Browser.EvaluateAsync\"}";
            var result2 = (bool)method!.Invoke(connector, new object?[] { jsonMethodPrefix, null })!;
            Assert.True(result2);

            // Test case: not for me
            string jsonNotForMe = "{\"source\": \"Host\", \"method\": \"Host.Hello\"}";
            var result3 = (bool)method!.Invoke(connector, new object?[] { jsonNotForMe, null })!;
            Assert.False(result3);
        }

        [Fact]
        public async Task ResolveTypeAsync_BranchHandling_ResolvesCorrectly()
        {
            var connector = FormatterServices.GetUninitializedObject(typeof(BrowserConnector)) as BrowserConnector;
            Assert.NotNull(connector);

            var resolveMethod = typeof(BrowserConnector).GetMethod("ResolveTypeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(resolveMethod);

            // Should return `this` for "WebView" and "Host" classes
            var task1 = (Task<object?>)resolveMethod!.Invoke(connector, new object?[] { null, null, "WebView", "EvaluateAsync", null, null })!;
            var result1 = await task1;
            Assert.Same(connector, result1);

            var task2 = (Task<object?>)resolveMethod!.Invoke(connector, new object?[] { null, null, "Host", "AnyMethod", null, null })!;
            var result2 = await task2;
            Assert.Same(connector, result2);

            // Should return null for other classes
            var task3 = (Task<object?>)resolveMethod!.Invoke(connector, new object?[] { null, null, "OtherClass", "Method", null, null })!;
            var result3 = await task3;
            Assert.Null(result3);
        }
    }
}
