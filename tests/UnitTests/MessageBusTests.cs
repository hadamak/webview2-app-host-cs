using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using WebView2AppHost;

namespace HostTests
{
    public class MessageBusTests
    {
        [Fact]
        public void Publish_ToEmptyBus_DoesNotThrow()
        {
            var bus = new MessageBus();
            var dummyConnector = new DummyConnector();
            bus.Register(dummyConnector);
            
            bus.Publish("{\"msg\": \"hello\"}");
            
            bus.Dispose();
        }

        [Fact]
        public void Dispose_WhilePublishing_HandlesGracefully()
        {
            var dummyConnector = new DummyConnector();
            var bus = new MessageBus();
            bus.Register(dummyConnector);

            bus.Dispose();
            
            var exception = Record.Exception(() => bus.Publish("{\"msg\": \"late\"}"));
            Assert.Null(exception);
        }

        private class DummyConnector : IConnector
        {
            public string Name => "Dummy";
            public Action<string> Publish { set { } }
            public void Deliver(string messageJson, Dictionary<string, object>? messageDict) { }
            public void Dispose() { }
        }
    }
}
