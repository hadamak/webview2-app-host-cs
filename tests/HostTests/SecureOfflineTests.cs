using System;
using Xunit;
using System.IO;
using System.Linq;
using System.Reflection;
using WebView2AppHost;

namespace HostTests
{
    public class SecureOfflineTests
    {
#if SECURE_OFFLINE
        [Fact]
        public void AppConfig_IsSecureMode_ReturnsTrue_InSecureBuilds()
        {
            Assert.True(AppConfig.IsSecureMode, "Secure mode flag should be enabled");
        }

        [Fact]
        public void ConnectorFactoryTests()
        {
            var config = LoadConfig(
                @"{""connectors"":[{""type"":""sidecar"",""alias"":""BlockedSidecar"",""executable"":""node""},{""type"":""dll"",""alias"":""AllowedDll"",""path"":""dummy.dll""}]}");
            var names = ConnectorFactory.GetAvailableConnectorNames(config, enableMcp: true);

            Assert.True(names.Contains("Browser"), "ConnectorFactory: Browser should remain available");
            Assert.True(names.Contains("Host"), "ConnectorFactory: Host should remain available");
            Assert.True(!names.Any(n => n.IndexOf("Mcp", StringComparison.OrdinalIgnoreCase) >= 0),
                "ConnectorFactory: MCP connectors must be absent");
            Assert.True(!names.Any(n => n.IndexOf("Pipe", StringComparison.OrdinalIgnoreCase) >= 0),
                "ConnectorFactory: Pipe connectors must be absent");
            Assert.True(!names.Any(n => n.IndexOf("Sidecar", StringComparison.OrdinalIgnoreCase) >= 0),
                "ConnectorFactory: Sidecar connectors must be absent");
        }

        [Fact]
        public void SecureBinarySymbolTests()
        {
            var assemblyPath = ResolveSecureHostAssemblyPath();
            if (!File.Exists(assemblyPath))
            {
                // Skip gracefully
                return;
            }

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

            Assert.True(foundTypes.Count == 0,
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
#else
        [Fact]
        public void AppConfig_IsSecureMode_ReturnsFalse_InStandardBuilds()
        {
            Assert.False(AppConfig.IsSecureMode, "Secure mode flag should be disabled in standard builds");
        }
#endif
    }
}