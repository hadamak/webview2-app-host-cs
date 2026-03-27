using System;
using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// Steam ブリッジのエントリーポイント。
    ///
    /// WebView2AppHost.Steam.dll が EXE と同じフォルダに存在する場合のみ
    /// リフレクションで SteamBridgeImpl を生成し、存在しない場合は null を
    /// 返すことで Steam なし環境でもクラッシュしない。
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
        /// WebView2AppHost.Steam.dll が存在する場合のみ初期化する。
        /// DLL が欠けている場合は null を返す（アプリはクラッシュしない）。
        /// </summary>
        public static SteamBridge? TryCreate(WebView2 webView, string appId, bool isDev)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var steamDll = Path.Combine(baseDir, "WebView2AppHost.Steam.dll");

            if (!File.Exists(steamDll))
            {
                AppLog.Log("INFO", "SteamBridge.TryCreate",
                    "WebView2AppHost.Steam.dll が見つかりません。Steam 機能は無効です。");
                return null;
            }

            try
            {
                var asm = Assembly.LoadFrom(steamDll);
                var implType = asm.GetType("WebView2AppHost.SteamBridgeImpl");
                if (implType == null)
                {
                    AppLog.Log("WARN", "SteamBridge.TryCreate",
                        "WebView2AppHost.SteamBridgeImpl クラスが見つかりません。Steam 機能は無効です。");
                    return null;
                }

                var impl = (ISteamBridgeImpl)Activator.CreateInstance(implType, webView, appId, isDev)!;
                return new SteamBridge(impl);
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "SteamBridge.TryCreate",
                    "Steam 機能の初期化に失敗しました。Steam 機能は無効です。", ex);
                return null;
            }
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
