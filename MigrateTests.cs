using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MigrateTests
{
    class Program
    {
        static void Main(string[] args)
        {
            var files = new[]
            {
                "tests/HostTests/McpTests.cs",
                "tests/HostTests/SidecarTests.cs",
                "tests/HostTests/ConnectorQualityTests.cs",
                "tests/HostTests/SecureOfflineTests.cs"
            };

            foreach (var file in files)
            {
                if (!File.Exists(file)) continue;

                var content = File.ReadAllText(file, Encoding.UTF8);

                // 1. Add using Xunit;
                if (!content.Contains("using Xunit;"))
                {
                    content = content.Replace("using System;", "using System;\r\nusing Xunit;");
                }

                // 2. Add IDisposable and Constructor/Dispose for the class
                var classRegex = new Regex(@"internal static class (\w+Tests)");
                string className = "";
                var match = classRegex.Match(content);
                if (match.Success)
                {
                    className = match.Groups[1].Value;
                    content = content.Replace(match.Value, $"public class {className} : IDisposable");
                }

                // 3. Replace RunAll method with Constructor and Dispose
                var runAllPattern = new Regex(@"(?s)internal static void RunAll\(\).*?\{.*?(var old = AppLog\.Override;.*?AppLog\.Override = TextWriter\.Null;).*?try\s*\{.*?\}.*?finally\s*\{.*?\}.*?\}");
                var constructorAndDispose = $"private readonly System.IO.TextWriter _oldLog;\r\n\r\n        public {className}()\r\n        {{\r\n            _oldLog = AppLog.Override;\r\n            AppLog.Override = TextWriter.Null;\r\n        }}\r\n\r\n        public void Dispose()\r\n        {{\r\n            AppLog.Override = _oldLog;\r\n        }}";

                if (runAllPattern.IsMatch(content))
                {
                    content = runAllPattern.Replace(content, constructorAndDispose);
                }
                else
                {
                    // Fallback for simple RunAll
                    var simpleRunAll = new Regex(@"(?s)internal static void RunAll\(\)\s*\{.*?\}");
                    content = simpleRunAll.Replace(content, "");
                    content = content.Replace($"public class {className} : IDisposable", $"public class {className}");
                }

                // 4. Replace private static void RunXXXTests() with [Fact] public void XXXTests()
                content = Regex.Replace(content, @"private static void Run(\w+Tests)\(\)", "[Fact]\r\n        public void $1()");

                // 5. Replace Assert( -> Assert.True( (excluding the helper definition)
                var assertHelper = new Regex(@"(?s)private static void Assert\(bool cond,\s*string label\)\s*\{.*?\}");
                content = assertHelper.Replace(content, "");

                content = Regex.Replace(content, @"(?<!\.)\bAssert\(", "Assert.True(");

                File.WriteAllText(file, content, new UTF8Encoding(true)); // Writing back with BOM/UTF8 based on original file if possible, or UTF8 standard
            }
        }
    }
}
