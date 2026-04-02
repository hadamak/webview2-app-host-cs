using System;

namespace WebView2AppHost
{
    /// <summary>
    /// PluginManager に登録されるプラグインのインターフェース。
    ///
    /// <para>
    /// ホストとプラグインの境界は C# の基本型（string）のみで繋ぐ。
    /// ホスト固有の型（AppConfig、WebView2 等）はプラグインの引数に含めない。
    /// アセンブリ境界を越えた型同一性の問題を回避するため、
    /// サードパーティ製プラグイン DLL はリフレクション経由でのラッピングが必要。
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
        /// ホストから app.conf.json の内容を JSON 文字列として受け取り、初期化する。
        /// プラグインは内部で JSON をパースし、必要な情報だけを抽出する。
        /// ホストの型（AppConfig など）には一切依存しない。
        /// </summary>
        void Initialize(string configJson);

        /// <summary>
        /// WebView2 の WebMessageReceived から転送されるメッセージを処理する。
        /// source フィールドが自プラグイン宛でない場合は速やかに return すること。
        /// </summary>
        void HandleWebMessage(string webMessageJson);
    }
}
