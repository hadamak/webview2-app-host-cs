using System;
using System.IO;
using System.Linq;
using System.Reflection;
using WebView2AppHost;

namespace HostTests
{
    internal static class SecureOfflineTests
    {
        public static void RunAll()
        {
#if SECURE_OFFLINE
            Console.WriteLine("\n--- Secure Offline Tests ---");
            Assert(AppConfig.IsSecureMode, "Secure mode flag should be enabled");
            RunConnectorFactoryTests();
            RunSecureBinarySymbolTests();
            Console.WriteLine("  Secure offline tests passed.");
#else
            Assert(!AppConfig.IsSecureMode, "Secure mode flag should be disabled in standard builds");
#endif
        }

#if SECURE_OFFLINE
        private static void RunConnectorFactoryTests()
        {
            var config = LoadConfig(
                @"{""sidecars"":[{""alias"":""BlockedSidecar"",""executable"":""node""}],""loadDlls"":[{""alias"":""AllowedDll"",""dll"":""dummy.dll""}]}");
            var names = ConnectorFactory.GetAvailableConnectorNames(config, enableMcp: true);

            Assert(names.Contains("Browser"), "ConnectorFactory: Browser should remain available");
            Assert(names.Contains("Host"), "ConnectorFactory: Host should remain available");
            Assert(!names.Any(n => n.IndexOf("Mcp", StringComparison.OrdinalIgnoreCase) >= 0),
                "ConnectorFactory: MCP connectors must be absent");
            Assert(!names.Any(n => n.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0),
                "ConnectorFactory: Pipe connectors must be absent");
            Assert(!names.Any(n => n.IndexOf("Sidecar", StringComparison.OrdinalIgnoreCase) >= 0),
                "ConnectorFactory: Sidecar connectors must be absent");
        }

        private static void RunSecureBinarySymbolTests()
        {
            var assemblyPath = ResolveSecureHostAssemblyPath();
            Assert(File.Exists(assemblyPath), $"Secure host binary not found: {assemblyPath}");

            var assembly = Assembly.LoadFrom(assemblyPath);
            var prohibitedTypeNames = new[]
            {
                "WebView2AppHost.McpBridge",
                "WebView2AppHost.McpConnector",
                "WebView2AppHost.SidecarConnector",
                "WebView2AppHost.PipeClientConnector",
                "WebView2AppHost.PipeServerConnector",
                "WebView2AppHost.CdpProxyHandler",
            };

            var foundTypes = assembly
                .GetTypes()
                .Where(t => Array.IndexOf(prohibitedTypeNames, t.FullName ?? string.Empty) >= 0)
                .Select(t => t.FullName ?? t.Name)
                .ToList();

            Assert(foundTypes.Count == 0,
                "Prohibited types found in secure build: " + string.Join(", ", foundTypes));
        }

        private static string ResolveSecureHostAssemblyPath()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    "src",
                    "bin",
                    "x64",
                    "SecureRelease",
                    "net48",
                    "WebView2AppHost.exe");

                if (File.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "src",
                "bin",
                "x64",
                "SecureRelease",
                "net48",
                "WebView2AppHost.exe");
        }

        private static AppConfig LoadConfig(string json)
        {
            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            {
                return AppConfig.Load(ms) ?? new AppConfig();
            }
        }
#endif

        private static void Assert(bool cond, string label)
        {
            if (!cond) throw new Exception("FAILED: " + label);
        }
    }
}
