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
    ///
    /// <para>
    /// 各コネクターに専用の送信キュー（ConnectorMailbox）を割り当て、
    /// メッセージの配信順序を保証しつつ、遅いコネクターが他のコネクターの
    /// 配信をブロックしない設計。
    /// </para>
    /// </summary>
    public sealed class MessageBus : IDisposable
    {
        private readonly List<(IConnector Connector, ConnectorMailbox Mailbox)> _connectors =
            new List<(IConnector, ConnectorMailbox)>();
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
            var mailbox = new ConnectorMailbox(connector);
            lock (_lock) _connectors.Add((connector, mailbox));
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
            List<(IConnector Connector, ConnectorMailbox Mailbox)> snapshot;
            lock (_lock) snapshot = new List<(IConnector, ConnectorMailbox)>(_connectors);

            // JSON を一度だけパース
            Dictionary<string, object>? dict = null;
            try { dict = s_json.Deserialize<Dictionary<string, object>>(json); }
            catch { /* パース失敗時は null のまま渡す */ }

            // 送信元以外の全コネクタに配信。コネクター単位の順序を保証。
            foreach (var (connector, mailbox) in snapshot)
            {
                if (connector == sender) continue;
                mailbox.Enqueue(json, dict);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();

            List<(IConnector Connector, ConnectorMailbox Mailbox)> snapshot;
            lock (_lock)
            {
                snapshot = new List<(IConnector, ConnectorMailbox)>(_connectors);
                _connectors.Clear();
            }

            foreach (var (connector, mailbox) in snapshot)
            {
                try { mailbox.Dispose(); } catch { }
                try { connector.Dispose(); } catch { }
            }
        }

        // ---------------------------------------------------------------
        // コネクター単位の送信キュー
        // ---------------------------------------------------------------

        /// <summary>
        /// 各コネクターに1つずつ割り当てられるメールボックス。
        /// 専用の背景スレッドがキューから順序通りにメッセージを取り出して
        /// Deliver を呼ぶため、配信順序が保証される。
        /// 遅いコネクターがあっても他のコネクターのメールボックスには影響しない。
        /// </summary>
        private sealed class ConnectorMailbox : IDisposable
        {
            private readonly IConnector _connector;
            private readonly BlockingCollection<(string Json, Dictionary<string, object>? Dict)> _mailbox =
                new BlockingCollection<(string, Dictionary<string, object>?)>(
                    new ConcurrentQueue<(string, Dictionary<string, object>?)>());
            private readonly Thread _deliverThread;

            public ConnectorMailbox(IConnector connector)
            {
                _connector = connector;
                _deliverThread = new Thread(DeliverLoop)
                {
                    IsBackground = true,
                    Name = $"Mailbox[{connector.Name}]"
                };
                _deliverThread.Start();
            }

            public void Enqueue(string json, Dictionary<string, object>? dict)
            {
                if (_mailbox.IsAddingCompleted) return;
                try { _mailbox.Add((json, dict)); }
                catch (InvalidOperationException) { }
            }

            private void DeliverLoop()
            {
                foreach (var (json, dict) in _mailbox.GetConsumingEnumerable())
                {
                    try
                    {
                        _connector.Deliver(json, dict);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log("ERROR", $"MessageBus -> [{_connector.Name}]", ex.Message, ex);
                    }
                }
            }

            public void Dispose()
            {
                _mailbox.CompleteAdding();
                _mailbox.Dispose();
            }
        }
    }
}
