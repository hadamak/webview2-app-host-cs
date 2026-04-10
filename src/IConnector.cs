using System;

namespace WebView2AppHost
{
    /// <summary>
    /// メッセージバスに接続するコネクターの共通インターフェース。
    ///
    /// <para>
    /// 従来の IHostPlugin（ホスト → プラグインの一方向）を
    /// 双方向の対称設計に置き換える。
    /// </para>
    ///
    /// <para>
    /// コネクターの種類とトランスポート:
    ///   DllConnector     … 同プロセス内 DLL（リフレクション呼び出し）
    ///   SidecarConnector … 子プロセス stdio（NDJSON）
    ///   BrowserConnector … WebView2 JS ↔ C#（PostWebMessage / WebMessageReceived）
    ///   McpConnector     … MCP クライアント stdio（JSON-RPC 2.0）
    /// </para>
    ///
    /// <para>
    /// データフロー:
    ///   外部 → Deliver(json) → コネクターが処理
    ///   コネクター → Publish(json) → MessageBus が他の全コネクターに配信
    /// </para>
    /// </summary>
    public interface IConnector : IDisposable
    {
        /// <summary>
        /// コネクターの識別名。
        /// MessageBus がメッセージのルーティングに使用する。
        /// DLL / サイドカーの場合は alias（例: "SQLite", "NodeBackend"）。
        /// </summary>
        string Name { get; }

        /// <summary>
        /// MessageBus が登録時に設定するデリゲート。
        /// コネクターはここを通じてバスにメッセージを送る。
        /// </summary>
        Action<string> Publish { set; }

        /// <summary>
        /// MessageBus がこのコネクター宛のメッセージを配信するときに呼ぶ。
        /// コネクターは自分が処理すべきものだけを扱い、それ以外は無視する。
        /// </summary>
        void Deliver(string messageJson);
    }
}
