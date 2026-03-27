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
        // ISteamBridgeImpl は EXE と Steam DLL の両方にソースリンクでコンパイルされるため、
        // 型同一性が異なる別の型として扱われる。Assembly.LoadFrom 経由でロードした
        // SteamBridgeImpl インスタンスを ISteamBridgeImpl へキャストすると
        // InvalidCastException になるため、object で保持してリフレクション経由で呼び出す。
        // IDisposable は mscorlib 由来で共有されるためキャストは問題なく動く。
        private readonly object? _impl;
        private bool _disposed;

        private SteamBridge(object impl) => _impl = impl;

        // ---------------------------------------------------------------------------
        // 静的ファクトリ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// WebView2AppHost.Steam.dll が存在する場合のみ初期化する。
        /// DLL が欠けている場合は null を返す（アプリはクラッシュしない）。
        ///
        /// <para>
        /// <see cref="InvalidOperationException"/> に <c>"STEAM_RESTART_REQUIRED"</c> メッセージが
        /// 付いた場合は Steam による再起動が必要なことを意味する。
        /// 呼び出し元（App.TryInitSteam）はこの例外をキャッチして Application.Exit() を呼ぶこと。
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// SteamAPI_RestartAppIfNecessary が true を返した場合（メッセージ = "STEAM_RESTART_REQUIRED"）。
        /// </exception>
        public static SteamBridge? TryCreate(WebView2 webView, string appId, bool isDev)
        {
            var baseDir  = AppDomain.CurrentDomain.BaseDirectory;
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

                var impl = Activator.CreateInstance(implType, webView, appId, isDev)!;
                return new SteamBridge(impl);
            }
            catch (TargetInvocationException tie)
                when (tie.InnerException is InvalidOperationException ioe
                      && ioe.Message == "STEAM_RESTART_REQUIRED")
            {
                // SteamBridgeImpl コンストラクタ内で RestartAppIfNecessary が true を返した。
                // Steam がアプリを再起動するためプロセスを終了しなければならない。
                // リフレクション境界を越えて呼び出し元に通知するため、メッセージを保ちつつ再スロー。
                AppLog.Log("INFO", "SteamBridge.TryCreate",
                    "Steam による再起動が必要なため初期化を中断します。");
                throw new InvalidOperationException("STEAM_RESTART_REQUIRED", tie.InnerException);
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
            // ISteamBridgeImpl は型同一性の問題でキャストできないため、リフレクションで呼び出す。
            _impl?.GetType()
                  .GetMethod("HandleWebMessage", new[] { typeof(string) })
                  ?.Invoke(_impl, new object[] { webMessageJson });
        }

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // IDisposable は mscorlib 由来のためアセンブリ境界を越えてキャスト可能。
            (_impl as IDisposable)?.Dispose();
        }
    }
}
