using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebView2AppHost
{
    /// <summary>
    /// コネクター間のメッセージを仲介する中央バス。
    ///
    /// <para>
    /// 設計方針:
    ///   - コネクターはバスに登録するだけ。互いを知らない。
    ///   - あるコネクターが Publish したメッセージは、他の全コネクターに Deliver される。
    ///   - コネクター側が「自分宛かどうか」をフィルタする責務を持つ。
    /// </para>
    ///
    /// <para>
    /// ルーティング:
    ///   特定コネクター宛のリクエスト（source / method prefix が一致）は
    ///   そのコネクターだけが処理する（他のコネクターも受け取るが無視する）。
    ///   応答（result / error）は全コネクターに届き、id で相関する側がピックアップする。
    ///   この設計により、AI が操作した結果がブラウザに即座に反映される。
    /// </para>
    /// </summary>
    public sealed class MessageBus : IDisposable
    {
        private readonly List<IConnector> _connectors = new List<IConnector>();
        private readonly object _lock = new object();
        private bool _disposed;

        // -------------------------------------------------------------------
        // 登録
        // -------------------------------------------------------------------

        /// <summary>
        /// コネクターを登録する。
        /// 登録時に Publish デリゲートを設定する。
        /// </summary>
        public void Register(IConnector connector)
        {
            if (connector == null) throw new ArgumentNullException(nameof(connector));

            // Publish = バスへの送信口。送信元コネクター自身には届けない。
            connector.Publish = json => Broadcast(json, sender: connector);

            lock (_lock) _connectors.Add(connector);

            AppLog.Log("INFO", "MessageBus", $"コネクター登録: {connector.Name}");
        }

        // -------------------------------------------------------------------
        // 外部からの入力（コネクター以外のソース）
        // -------------------------------------------------------------------

        /// <summary>
        /// コネクター以外のソース（例: app 起動時の初期化メッセージ）から
        /// バスにメッセージを投入する。全コネクターに配信される。
        /// </summary>
        public void Dispatch(string messageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(messageJson)) return;
            Broadcast(messageJson, sender: null);
        }

        // -------------------------------------------------------------------
        // 内部ブロードキャスト
        // -------------------------------------------------------------------

        private void Broadcast(string messageJson, IConnector? sender)
        {
            if (_disposed) return;

            AppLog.Log(
                AppLog.LogLevel.Debug,
                "MessageBus",
                $"Broadcast from {(sender?.Name ?? "N/A")}: {AppLog.DescribeMessageJson(messageJson)}",
                dataKind: AppLog.LogDataKind.Sensitive);

            List<IConnector> snapshot;
            lock (_lock) snapshot = new List<IConnector>(_connectors);

            var tasks = snapshot
                .Where(c => c != sender)
                .Select(c => Task.Run(() =>
                {
                    try { c.Deliver(messageJson); }
                    catch (Exception ex)
                    {
                        AppLog.Log("ERROR", $"MessageBus → [{c.Name}]", ex.Message, ex);
                    }
                }));

            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }

        // -------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            List<IConnector> snapshot;
            lock (_lock)
            {
                snapshot = new List<IConnector>(_connectors);
                _connectors.Clear();
            }

            foreach (var c in snapshot)
            {
                try { c.Dispose(); }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", "MessageBus.Dispose",
                        $"[{c.Name}] Dispose 失敗: {ex.Message}");
                }
            }
        }
    }
}
