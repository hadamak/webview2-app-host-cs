using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using WebView2AppHost;
using System.Net.Http;

namespace HostTests
{
    public class CdpProxyTests
    {
        [Fact]
        public void IsHopByHopHeader_IdentifiesRestrictedHeaders()
        {
            var method = typeof(CdpProxyHandler).GetMethod("IsHopByHopHeader", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            Assert.True((bool)method!.Invoke(null, new[] { "Host" })!);
            Assert.True((bool)method!.Invoke(null, new[] { "host" })!); // Case insensitive
            Assert.True((bool)method!.Invoke(null, new[] { "Transfer-Encoding" })!);
            Assert.True((bool)method!.Invoke(null, new[] { "Upgrade" })!);

            Assert.False((bool)method!.Invoke(null, new[] { "Content-Type" })!);
            Assert.False((bool)method!.Invoke(null, new[] { "X-Custom-Header" })!);
        }

        [Fact]
        public void EscapeJsonString_EscapesCorrectly()
        {
            var method = typeof(CdpProxyHandler).GetMethod("EscapeJsonString", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            Assert.Equal("hello \\\"world\\\"", (string)method!.Invoke(null, new[] { "hello \"world\"" })!);
            Assert.Equal("path\\\\to\\\\file", (string)method!.Invoke(null, new[] { "path\\to\\file" })!);
            Assert.Equal("line1\\nline2", (string)method!.Invoke(null, new[] { "line1\nline2" })!);
        }

        [Fact]
        public void AddFilteredHeaders_FiltersRestrictedAndCorsHeaders()
        {
            var method = typeof(CdpProxyHandler).GetMethod("AddFilteredHeaders", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var dest = new List<string>();
            var response = new HttpResponseMessage();
            response.Headers.Add("Host", "example.com"); // Hop-by-hop
            response.Headers.Add("Access-Control-Allow-Origin", "*"); // CORS
            response.Headers.Add("X-Normal", "Value");
            response.Content = new StringContent("test"); // Sets Content-Length

            method!.Invoke(null, new object[] { dest, response.Headers });
            method!.Invoke(null, new object[] { dest, response.Content.Headers });

            // Should contain X-Normal and Content-Type.
            // Access-Control-* and Content-Length and Host should be filtered.
            Assert.Equal(2, dest.Count);
            Assert.Contains("X-Normal", dest.Find(x => x.Contains("X-Normal"))!);
            Assert.Contains("Content-Type", dest.Find(x => x.Contains("Content-Type"))!);
        }

        [Fact]
        public void ParseEvent_DeserializesValidJson()
        {
            // CdpProxyHandler constructor requires CoreWebView2 which is hard to mock.
            // But we can use FormatterServices to get an uninitialized object for logic-only testing.
            var handler = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(CdpProxyHandler)) as CdpProxyHandler;
            Assert.NotNull(handler);

            // Initialize the serializer field via reflection since constructor didn't run
            var serializerField = typeof(CdpProxyHandler).GetField("_eventSerializer", BindingFlags.NonPublic | BindingFlags.Instance);
            var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(
                typeof(CdpProxyHandler).GetNestedType("CdpFetchRequestPausedParams", BindingFlags.NonPublic),
                new System.Runtime.Serialization.Json.DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            serializerField!.SetValue(handler, serializer);

            var parseMethod = typeof(CdpProxyHandler).GetMethod("ParseEvent", BindingFlags.NonPublic | BindingFlags.Instance);
            
            string json = "{\"requestId\":\"req123\",\"request\":{\"url\":\"https://api.test/\",\"method\":\"POST\",\"headers\":{\"Content-Type\":\"application/json\"},\"postData\":\"{\\\"a\\\":1}\",\"hasPostData\":true}}";
            var result = parseMethod!.Invoke(handler, new[] { json });
            
            Assert.NotNull(result);
            Assert.Equal("req123", result!.GetType().GetProperty("RequestId")?.GetValue(result));
            
            var request = result.GetType().GetProperty("Request")?.GetValue(result);
            Assert.NotNull(request);
            Assert.Equal("POST", request!.GetType().GetProperty("Method")?.GetValue(request));
            Assert.Equal("https://api.test/", request.GetType().GetProperty("Url")?.GetValue(request));
        }
    }
}
