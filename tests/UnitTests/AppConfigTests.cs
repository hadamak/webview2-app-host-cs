using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class AppConfigTests : IDisposable
    {
        private readonly string _workDir;

        public AppConfigTests()
        {
            _workDir = Path.Combine(Path.GetTempPath(), "webview2-app-host-config-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_workDir, recursive: true); } catch { /* ignore */ }
        }

        private AppConfig? LoadConfig(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return AppConfig.Load(ms);
            }
        }

        [Fact]
        public void Load_WithValidTitle_SetsTitleProperty()
        {
            var cfg = LoadConfig("{\"title\":\"Test\"}");
            Assert.NotNull(cfg);
            Assert.Equal("Test", cfg!.Title);
        }

        [Fact]
        public void Load_WithTooSmallWindowSize_SanitizesToMinimum()
        {
            var cfg = LoadConfig("{\"window\":{\"width\":10}}");
            Assert.NotNull(cfg);
            Assert.True(cfg!.Width >= 160, "Config should sanitize width to at least minimum allowed");
        }

        [Fact]
        public void ApplyUserConfig_WithOverrideFile_AppliesOverride()
        {
            var dir = Path.Combine(_workDir, "userconf");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "user.conf.json"), "{\"width\":1920}");
            
            var cfg = new AppConfig();
            cfg.ApplyUserConfig(dir);
            
            Assert.Equal(1920, cfg.Width);
        }

        [Fact]
        public void IsProxyAllowed_WithAllowedOrigin_ReturnsTrue()
        {
            var cfg = LoadConfig("{\"proxy_origins\":[\"https://api.test\"]}");
            Assert.NotNull(cfg);
            Assert.True(cfg!.IsProxyAllowed(new Uri("https://api.test/v1")));
        }

        [Fact]
        public void Load_WithMultipleProxyOrigins_ParsesCorrectly()
        {
            var cfg = LoadConfig("{\"proxy_origins\":[\"https://a\",\"https://b\"]}");
            Assert.NotNull(cfg);
            Assert.Equal(2, cfg!.ProxyOrigins.Length);
        }

        [Fact]
        public void Load_WithStructuredConfig_ParsesPropertiesProperly()
        {
            var json = @"
            {
              ""window"": { ""width"": 1440, ""height"": 900, ""frame"": false },
              ""url"": ""https://app.local/dashboard.html"",
              ""proxy_origins"": [""https://api.github.com""],
              ""steam"": { ""app_id"": ""480"", ""dev_mode"": true },
              ""navigation_policy"": {
                ""external_navigation_mode"": ""rules"",
                ""open_in_browser"": [""*.github.com""],
                ""block_request_patterns"": [""*ads*""]
              },
              ""connectors"": [
                { ""type"": ""browser"" },
                { ""type"": ""dll"", ""alias"": ""Steam"", ""path"": ""Facepunch.Steamworks.Win64.dll"" },
                { ""type"": ""sidecar"", ""runtime"": ""node"", ""script"": ""agent.js"" }
              ]
            }";

            var cfg = LoadConfig(json);
            Assert.NotNull(cfg);
            Assert.Equal(1440, cfg!.Width);
            Assert.Equal(900, cfg.Height);
            Assert.False(cfg.Frame);
            Assert.Equal("https://app.local/dashboard.html", cfg.Url);
            
            Assert.Single(cfg.ProxyOrigins);
            Assert.Equal("https://api.github.com", cfg.ProxyOrigins[0]);
            
            Assert.Equal("480", cfg.SteamAppId);
            Assert.True(cfg.SteamDevMode);
            
            Assert.Single(cfg.LoadDlls);
            Assert.Equal("Steam", cfg.LoadDlls[0].Alias);

#if !SECURE_OFFLINE
            Assert.Single(cfg.Sidecars);
            Assert.Equal("Node", cfg.Sidecars[0].Alias);
#endif
            Assert.True(cfg.ShouldOpenInBrowser("api.github.com"));
            Assert.True(cfg.IsRequestBlocked("https://cdn.example.com/ads/banner.js"));

            var directNames = ConnectorFactory.GetAvailableConnectorNames(cfg, enableMcp: false);
            Assert.Contains("Browser", directNames);
            Assert.Contains("Host", directNames);
#if !SECURE_OFFLINE
            Assert.Contains("Node", directNames);

            var directCfg = LoadConfig(@"{
              ""connectors"": [
                { ""type"": ""browser"" },
                { ""type"": ""pipe_server"" },
                { ""type"": ""mcp"" },
                { ""type"": ""dll"", ""alias"": ""Steam"", ""path"": ""Facepunch.Steamworks.Win64.dll"", ""expose_events"": [""OnGameOverlayActivated""] },
                { ""type"": ""sidecar"", ""alias"": ""PythonRuntime"", ""executable"": ""python"", ""working_directory"": ""python-runtime"", ""wait_for_ready"": true }
              ]
            }");
            Assert.NotNull(directCfg);
            Assert.Single(directCfg!.LoadDlls);
            Assert.Equal("Facepunch.Steamworks.Win64.dll", directCfg.LoadDlls[0].Dll);
            
            Assert.Single(directCfg.Sidecars);
            Assert.Equal("python-runtime", directCfg.Sidecars[0].WorkingDirectory);

            var directConnectorNames = ConnectorFactory.GetAvailableConnectorNames(directCfg, enableMcp: false);
            Assert.Contains("Browser", directConnectorNames);
            Assert.Contains("Host", directConnectorNames);
            Assert.Contains("PythonRuntime", directConnectorNames);
            Assert.Contains("PipeServer", directConnectorNames);
            Assert.Contains("Mcp", directConnectorNames);
#endif
        }

        [Fact]
        public void NormalizeConnectors_WithDuplicateAliases_KeepsFirstOrThrows()
        {
            var json = @"
            {
              ""connectors"": [
                { ""type"": ""dll"", ""alias"": ""Test"", ""path"": ""first.dll"" },
                { ""type"": ""dll"", ""alias"": ""Test"", ""path"": ""second.dll"" }
              ]
            }";
            var cfg = LoadConfig(json);
            Assert.NotNull(cfg);
            // Verify that only the first alias is retained or it handles dupes gracefully depending on AppConfig implementation.
            // Currently AppConfig uses aliases for dictionaries, so we just want to ensure it parses without crashing,
            // or the first one wins.
            Assert.Contains(cfg!.LoadDlls, x => x.Alias == "Test");
            Assert.Equal(1, cfg.LoadDlls.Count(x => x.Alias == "Test"));
        }

        [Fact]
        public void NormalizeConnectors_WithInvalidPaths_BoundaryValues()
        {
            var json = @"
            {
              ""connectors"": [
                { ""type"": ""dll"", ""alias"": ""EmptyPath"", ""path"": """" },
                { ""type"": ""sidecar"", ""alias"": ""EmptyExe"", ""executable"": """" }
              ]
            }";
            var cfg = LoadConfig(json);
            Assert.NotNull(cfg);
            // Assuming empty paths are either ignored or loaded as is without throwing exception during load.
            Assert.Empty(cfg!.LoadDlls);
#if !SECURE_OFFLINE
            Assert.Empty(cfg.Sidecars);
#endif
        }

#if !SECURE_OFFLINE
        [Fact]
        public void ConnectorFactory_BuildHeadless_BuildsCorrectly()
        {
            var json = @"
            {
              ""connectors"": [
                { ""type"": ""dll"", ""alias"": ""Test"", ""path"": ""test.dll"" },
                { ""type"": ""sidecar"", ""alias"": ""Sidecar"", ""executable"": ""node"" }
              ]
            }";
            var cfg = LoadConfig(json);
            Assert.NotNull(cfg);
            
            var (bus, mcp) = ConnectorFactory.BuildHeadless(cfg!, CancellationToken.None);
            
            Assert.NotNull(bus);
            Assert.NotNull(mcp);
        }
#endif
    }
}
