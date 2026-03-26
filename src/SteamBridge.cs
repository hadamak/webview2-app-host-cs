using System;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// Facepunch.Steamworks を用いた Steam ブリッジのエントリーポイント。
    ///
    /// このクラス自体は Facepunch.Steamworks の型を直接参照しない。
    /// DLL が存在する場合のみ <see cref="SteamBridgeImpl"/> を生成し、
    /// 存在しない場合は null を返すことで Steam なし環境でもクラッシュしない。
    ///
    /// JS ↔ C# メッセージフォーマット:
    ///   JS → C#: {"source":"steam","messageId":"invoke",
    ///              "params":{"className":"SteamUserStats","methodName":"SetAchievement","args":["ACH_WIN"]},
    ///              "asyncId":1}
    ///   C# → JS: {"source":"steam","messageId":"invoke-result","result":<value>,"asyncId":1}
    ///   C# → JS (イベント): {"source":"steam","event":"OnAchievementProgress","params":{...}}
    /// </summary>
    internal sealed class SteamBridge : IDisposable
    {
        private readonly ISteamBridgeImpl? _impl;
        private bool _disposed;

        private SteamBridge(ISteamBridgeImpl impl) => _impl = impl;

        // ---------------------------------------------------------------------------
        // 静的ファクトリ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Facepunch.Steamworks.Win64.dll と steam_api64.dll が存在する場合のみ初期化する。
        /// いずれかが欠けている場合は null を返す（アプリはクラッシュしない）。
        /// </summary>
        public static SteamBridge? TryCreate(WebView2 webView, string appId, bool isDev)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var facepunchDll = Path.Combine(baseDir, "Facepunch.Steamworks.Win64.dll");
            var steamApiDll  = Path.Combine(baseDir, "steam_api64.dll");

            if (!File.Exists(facepunchDll))
            {
                AppLog.Log("INFO", "SteamBridge.TryCreate",
                    "Facepunch.Steamworks.Win64.dll が見つかりません。Steam 機能は無効です。");
                return null;
            }

            if (!File.Exists(steamApiDll))
            {
                AppLog.Log("INFO", "SteamBridge.TryCreate",
                    "steam_api64.dll が見つかりません。Steam 機能は無効です。");
                return null;
            }

            try
            {
                return CreateWithImpl(webView, appId, isDev);
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "SteamBridge.TryCreate",
                    "Steam 機能の初期化に失敗しました。Steam 機能は無効です。", ex);
                return null;
            }
        }

        /// <summary>
        /// NoInlining により、JIT がこのメソッドを処理するのは実際に呼び出されたときだけ。
        /// SteamBridgeImpl への参照（＝Facepunch.Steamworks アセンブリのロード）は
        /// File.Exists チェック通過後にのみ発生する。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SteamBridge CreateWithImpl(WebView2 webView, string appId, bool isDev)
        {
            var impl = new SteamBridgeImpl(webView, appId, isDev);
            return new SteamBridge(impl);
        }

        // ---------------------------------------------------------------------------
        // 公開 API
        // ---------------------------------------------------------------------------

        /// <summary>
        /// WebView2 の WebMessageReceived から渡す。source が "steam" 以外は無視する。
        /// </summary>
        public void HandleWebMessage(string webMessageJson)
        {
            if (_disposed) return;
            _impl?.HandleWebMessage(webMessageJson);
        }

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _impl?.Dispose();
        }
    }
}
