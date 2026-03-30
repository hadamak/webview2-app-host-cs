using System;

namespace WebView2AppHost
{
    /// <summary>
    /// PluginManager に登録されるプラグインのインターフェース。
    ///
    /// <para>
    /// EXE 内で定義されるため、アセンブリ境界を越えた型同一性の問題は発生しない。
    /// サードパーティ製プラグイン DLL が本インターフェースを直接実装する場合は
    /// リフレクション経由でのラッピングが必要（<see cref="ISteamBridgeImpl"/> と同様の理由）。
    /// </para>
    ///
    /// プラグインのメッセージフォーマット（JS → C#）:
    ///   { "source": "&lt;PluginName&gt;", "messageId": "invoke", "params": { ... }, "asyncId": N }
    ///   source の照合は大文字小文字を区別しないことを推奨する。
    /// </summary>
    public interface IHostPlugin : IDisposable
    {
        /// <summary>
        /// JS 側の source フィールドと照合するプラグイン名。
        /// 例: "Steam", "Node"
        /// </summary>
        string PluginName { get; }

        /// <summary>
        /// WebView2 の WebMessageReceived から転送されるメッセージを処理する。
        /// source フィールドが自プラグイン宛でない場合は速やかに return すること。
        /// </summary>
        void HandleWebMessage(string webMessageJson);
    }
}
