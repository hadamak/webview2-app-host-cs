using System;
using System.IO;
using System.Text;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class NavigationPolicyTests
    {
        private AppConfig? LoadConfig(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return AppConfig.Load(ms);
            }
        }

        [Fact]
        public void Classify_AppLocalUri_ReturnsAllow()
        {
            Assert.Equal(NavigationPolicy.Action.Allow, NavigationPolicy.Classify("https://app.local/index.html"));
        }

        [Fact]
        public void Classify_ExternalNavigationMode_Browser_ReturnsOpenExternal()
        {
            var browserCfg = LoadConfig(@"{
              ""navigation_policy"": {
                ""external_navigation_mode"": ""browser"",
                ""block"": [""blocked.example.com""],
                ""allowed_external_schemes"": [""https"", ""mailto""]
              }
            }");
            Assert.NotNull(browserCfg);

#if SECURE_OFFLINE
            Assert.Equal(NavigationPolicy.Action.Block, NavigationPolicy.Classify("https://example.com", browserCfg));
#else
            Assert.Equal(NavigationPolicy.Action.OpenExternal, NavigationPolicy.Classify("https://example.com", browserCfg));
            Assert.Equal(NavigationPolicy.Action.Block, NavigationPolicy.Classify("https://blocked.example.com", browserCfg));
            Assert.Equal(NavigationPolicy.Action.OpenExternal, NavigationPolicy.Classify("mailto:test@example.com", browserCfg));
#endif
        }

        [Fact]
        public void Classify_ExternalNavigationMode_Host_ReturnsAllow()
        {
            var hostCfg = LoadConfig(@"{
              ""navigation_policy"": {
                ""external_navigation_mode"": ""host"",
                ""allowed_external_schemes"": [""https""]
              }
            }");
            Assert.NotNull(hostCfg);

#if SECURE_OFFLINE
            Assert.Equal(NavigationPolicy.Action.Block, NavigationPolicy.Classify("https://api.github.com", hostCfg));
#else
            Assert.Equal(NavigationPolicy.Action.Allow, NavigationPolicy.Classify("https://api.github.com", hostCfg));
#endif
        }

        [Fact]
        public void Classify_ExternalNavigationMode_Rules_AppliesOpenInHostAndBrowserLists()
        {
            var rulesCfg = LoadConfig(@"{
              ""navigation_policy"": {
                ""external_navigation_mode"": ""rules"",
                ""open_in_host"": [""*.github.com""],
                ""open_in_browser"": [""example.com""],
                ""block"": [""blocked.example.com""],
                ""allowed_external_schemes"": [""https"", ""mailto""]
              }
            }");
            Assert.NotNull(rulesCfg);

#if SECURE_OFFLINE
            Assert.Equal(NavigationPolicy.Action.Block, NavigationPolicy.Classify("https://api.github.com", rulesCfg));
#else
            Assert.Equal(NavigationPolicy.Action.Allow, NavigationPolicy.Classify("https://api.github.com", rulesCfg));
            Assert.Equal(NavigationPolicy.Action.OpenExternal, NavigationPolicy.Classify("https://example.com", rulesCfg));
#endif
            Assert.Equal(NavigationPolicy.Action.Block, NavigationPolicy.Classify("https://blocked.example.com", rulesCfg));
        }

        [Fact]
        public void Classify_ExternalNavigationMode_Block_ReturnsBlock()
        {
            var blockCfg = LoadConfig(@"{ ""navigation_policy"": { ""external_navigation_mode"": ""block"" } }");
            Assert.NotNull(blockCfg);
            Assert.Equal(NavigationPolicy.Action.Block, NavigationPolicy.Classify("https://example.com", blockCfg));
        }

        [Fact]
        public void Classify_UnsupportedScheme_ReturnsBlock()
        {
#if SECURE_OFFLINE
            Assert.Equal(NavigationPolicy.Action.Block, NavigationPolicy.Classify("http://app.local/"));
#else
            Assert.Equal(NavigationPolicy.Action.OpenExternal, NavigationPolicy.Classify("http://app.local/"));
#endif
            Assert.Equal(NavigationPolicy.Action.Block, NavigationPolicy.Classify("customscheme://test"));
        }
    }
}
