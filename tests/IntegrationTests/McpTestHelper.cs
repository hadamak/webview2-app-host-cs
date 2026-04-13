using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WebView2AppHost;

namespace HostTests
{
    public static class McpTestHelper
    {
        private static readonly JavaScriptSerializer s_json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static async Task<List<string>> RunServerAsync(
            string[] inputLines,
            Action<string, McpConnector>? handleWebMessage = null,
            MockBrowserTools? mockBrowser = null,
            int timeoutMs = 2000,
            int callTimeoutMs = 300,
            Action<McpConnector>? configure = null)
        {
            var input = new StringReader(string.Join("\n", inputLines) + "\n");
            var outBuf = new StringBuilder();
            var output = new StringWriter(outBuf);
            var mcp = new McpConnector(null, input, output, TimeSpan.FromMilliseconds(callTimeoutMs));

            if (mockBrowser != null)
            {
                mcp.SetBrowser(mockBrowser);
            }

            configure?.Invoke(mcp);

            if (handleWebMessage != null)
            {
                mcp.Publish = reqJson => handleWebMessage(reqJson, mcp);
            }

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

        public static Dictionary<string, object> ParseJson(string json)
        {
            return s_json.Deserialize<Dictionary<string, object>>(json) ?? throw new Exception("JSON parse failed: " + json);
        }

        public static string BuildRequest(int id, string method, object? @params = null)
        {
            var obj = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };
            if (@params != null)
            {
                obj["params"] = @params;
            }
            return s_json.Serialize(obj);
        }

        public static string BuildToolsCallRequest(int id, string name, Dictionary<string, object>? arguments = null)
        {
            return BuildRequest(id, "tools/call", new Dictionary<string, object>
            {
                ["name"] = name,
                ["arguments"] = arguments ?? new Dictionary<string, object>()
            });
        }

        public static string ExtractToolTextContent(Dictionary<string, object> response)
        {
            var result = response["result"] as Dictionary<string, object>
                ?? throw new Exception("No result in response");
            var content = result["content"] as System.Collections.ArrayList
                ?? throw new Exception("No content in result");
            var first = content[0] as Dictionary<string, object>
                ?? throw new Exception("No first content item");
            return first["text"]?.ToString() ?? "";
        }

        public static bool IsErrorResponse(Dictionary<string, object> response)
        {
            var result = response["result"] as Dictionary<string, object>;
            return result?["isError"] as bool? == true;
        }

        public static string? GetErrorMessage(Dictionary<string, object> response)
        {
            if (IsErrorResponse(response))
            {
                return ExtractToolTextContent(response);
            }
            return null;
        }
    }
}
