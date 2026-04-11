using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class CdpProxyTests
    {
        [Fact]
        public void IsHopByHopHeader_IdentifiesRestrictedHeaders()
        {
            // IsHopByHopHeader is private static
            var method = typeof(CdpProxyHandler).GetMethod("IsHopByHopHeader", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            Assert.True((bool)method!.Invoke(null, new[] { "Host" })!);
            Assert.True((bool)method!.Invoke(null, new[] { "host" })!); // Case insensitive
            Assert.True((bool)method!.Invoke(null, new[] { "Transfer-Encoding" })!);
            Assert.True((bool)method!.Invoke(null, new[] { "Connection" })!);
            Assert.True((bool)method!.Invoke(null, new[] { "Upgrade" })!);

            Assert.False((bool)method!.Invoke(null, new[] { "Content-Type" })!);
            Assert.False((bool)method!.Invoke(null, new[] { "X-Test" })!);
        }
    }
}
