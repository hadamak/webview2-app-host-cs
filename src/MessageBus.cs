using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebView2AppHost
{
    /// <summary>
    /// コネクター間のメッセージを仲介する中央バス。
    /// 堅牢化のため、ブロードキャストを非同期キューで行う。
    /// </summary>
    public sealed class MessageBus : IDisposable
    {
        private readonly List<IConnector> _connectors = new List<IConnector>();
        private readonly object _lock = new object();
        private readonly BlockingCollection<(string Json, IConnector? Sender)> _queue = 
            new BlockingCollection<(string, IConnector?)>(new ConcurrentQueue<(string, IConnector?)>());
        
        private static readonly System.Web.Script.Serialization.JavaScriptSerializer s_json =
            new System.Web.Script.Serialization.JavaScriptSerializer();

        private readonly Thread _dispatchThread;
        private bool _disposed;

        public MessageBus()
        {
            _dispatchThread = new Thread(DispatchLoop) { IsBackground = true, Name = "MessageBusDispatch" };
            _dispatchThread.Start();
        }

        public void Register(IConnector connector)
        {
            if (connector == null) throw new ArgumentNullException(nameof(connector));
            connector.Publish = json => Publish(json, connector);
            lock (_lock) _connectors.Add(connector);
            AppLog.Log("INFO", "MessageBus", $"Connector registered: {connector.Name}");
        }

        public void Publish(string json, IConnector? sender = null)
        {
            if (_disposed || string.IsNullOrWhiteSpace(json)) return;
            try { _queue.Add((json, sender)); } catch (InvalidOperationException) { }
        }

        private void DispatchLoop()
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                BroadcastInternal(item.Json, item.Sender);
            }
        }

        private void BroadcastInternal(string json, IConnector? sender)
        {
            List<IConnector> snapshot;
            lock (_lock) snapshot = new List<IConnector>(_connectors);

            // JSON を一度だけパース
            Dictionary<string, object>? dict = null;
            try { dict = s_json.Deserialize<Dictionary<string, object>>(json); }
            catch { /* パース失敗時は null のまま渡す */ }

            // 送信元以外の全コネクタに配信。順序を維持しつつ非同期で実行。
            foreach (var connector in snapshot)
            {
                if (connector == sender) continue;
                
                var target = connector;
                var messageDict = dict;
                Task.Run(() =>
                {
                    try { target.Deliver(json, messageDict); }
                    catch (Exception ex)
                    {
                        AppLog.Log("ERROR", $"MessageBus -> [{target.Name}]", ex.Message, ex);
                    }
                });
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();

            List<IConnector> snapshot;
            lock (_lock)
            {
                snapshot = new List<IConnector>(_connectors);
                _connectors.Clear();
            }

            foreach (var c in snapshot)
            {
                try { c.Dispose(); } catch { }
            }
        }
    }
}
