using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Steamworks;
using Steamworks.Data;

namespace WebView2AppHost
{
    /// <summary>
    /// Facepunch.Steamworks を用いた Steam ブリッジ実体クラス。
    ///
    /// リフレクション・ディスパッチャの共通ロジックは ReflectionDispatcherBase に集約されている。
    /// 本クラスが担うのは Steam 固有の処理のみ。
    ///
    /// Steam 固有の責務:
    ///   - SteamClient の初期化 / RunCallbacks / Shutdown
    ///   - Steamworks 型のホワイトリスト管理と型解決 (ResolveTypeAsync)
    ///   - Steamworks 構造体 (AppId / SteamId など) の引数変換 (TryConvertArgExtra)
    ///   - TriggerScreenshot 特例（WebView2 キャプチャ → WriteScreenshot）
    ///   - Steam コールバック → JS イベント転送
    /// </summary>
    public sealed class SteamBridgeImpl : ReflectionDispatcherBase, ISteamBridgeImpl
    {
        internal const string SteamRestartRequiredMessage = "STEAM_RESTART_REQUIRED";

        private readonly System.Windows.Forms.Timer _callbackTimer;
        private readonly Assembly                    _steamworkAsm;
        private readonly HashSet<string>             _allowedClassNames;

        // ---------------------------------------------------------------------------
        // ReflectionDispatcherBase 実装
        // ---------------------------------------------------------------------------

        protected override string SourceName => "steam";

        protected override bool ShouldWrapAsHandle(object result)
        {
            var ns = result.GetType().Namespace;
            return ns != null && ns.StartsWith("Steamworks") && !result.GetType().IsEnum;
        }

        protected override object? TryConvertArgExtra(object? raw, Type targetType)
        {
            if (targetType == typeof(AppId))           return new AppId           { Value = Convert.ToUInt32(raw) };
            if (targetType == typeof(SteamId))         return new SteamId         { Value = Convert.ToUInt64(raw) };
            if (targetType == typeof(DepotId))         return new DepotId         { Value = Convert.ToUInt32(raw) };
            if (targetType == typeof(GameId))          return new GameId          { Value = Convert.ToUInt64(raw) };
            if (targetType == typeof(InventoryDefId))  return new InventoryDefId  { Value = Convert.ToInt32(raw)  };
            if (targetType == typeof(InventoryItemId)) return new InventoryItemId { Value = Convert.ToUInt64(raw) };
            return null;
        }

        protected override async Task<Type?> ResolveTypeAsync(
            Dictionary<string, object> paramsObj,
            string className, string methodName,
            object?[] argsRaw, double asyncId)
        {
            // TriggerScreenshot 特例 — 自前で処理して null を返す
            if (className == "SteamScreenshots" && methodName == "TriggerScreenshot")
            {
                await TriggerScreenshotAsync(asyncId);
                return null;
            }

            if (!_allowedClassNames.Contains(className))
                throw new TypeLoadException(
                    $"クラス '{className}' はホワイトリストに含まれていません。");

            return _steamworkAsm.GetType($"Steamworks.{className}")
                   ?? _steamworkAsm.GetType($"Steamworks.Data.{className}")
                   ?? throw new TypeLoadException(
                       $"Steamworks または Steamworks.Data に '{className}' が見つかりません。");
        }

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        public SteamBridgeImpl(WebView2 webView, string appId, bool isDev)
            : base(webView)
        {
            _steamworkAsm = typeof(SteamClient).Assembly;

            _allowedClassNames = new HashSet<string>(
                _steamworkAsm.GetExportedTypes()
                    .Where(t => t.Namespace == "Steamworks" || t.Namespace == "Steamworks.Data")
                    .Select(t => t.Name),
                StringComparer.Ordinal);

            if (!uint.TryParse(appId, out var id))
                throw new ArgumentException($"無効な AppID: {appId}");

            if (!isDev)
            {
                if (SteamClient.RestartAppIfNecessary(id))
                {
                    AppLog.Log("INFO", "SteamBridgeImpl",
                        "Steam 経由の起動ではありません。Steam による再起動を待機します。");
                    throw new InvalidOperationException(SteamRestartRequiredMessage);
                }
            }

            SteamClient.Init(id, asyncCallbacks: false);
#if DEBUG
            AppLog.Log("INFO", "SteamBridgeImpl", $"SteamClient 初期化完了 (appId={appId}, dev={isDev})");
#endif
            RegisterSteamCallbacks();

            _callbackTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _callbackTimer.Tick += (s, e) => { if (!_disposed) SteamClient.RunCallbacks(); };
            _callbackTimer.Start();
        }

        // ---------------------------------------------------------------------------
        // ISteamBridgeImpl
        // ---------------------------------------------------------------------------

        public void HandleWebMessage(string webMessageJson)
            => HandleWebMessageCore(webMessageJson);

        // ---------------------------------------------------------------------------
        // TriggerScreenshot 特例
        // ---------------------------------------------------------------------------

        private async Task TriggerScreenshotAsync(double asyncId)
        {
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            var tcs = new TaskCompletionSource<bool>();
            _webView.BeginInvoke(new Action(async () =>
            {
                try   { await CaptureAndSendScreenshotAsync(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));

            await tcs.Task.ConfigureAwait(false);
            SendResult(asyncId, null, null);
        }

        private async Task CaptureAndSendScreenshotAsync()
        {
            if (_disposed || _webView.CoreWebView2 == null) return;
            try
            {
                using var pngStream = new MemoryStream();
                await _webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, pngStream);
                pngStream.Position = 0;

                using var bmp = new Bitmap(pngStream);
                var (rgb, width, height) = BitmapToRgb(bmp);
                SteamScreenshots.WriteScreenshot(rgb, width, height);
#if DEBUG
                AppLog.Log("INFO", "SteamBridgeImpl.Screenshot", $"WriteScreenshot 完了 ({width}x{height})");
#endif
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridgeImpl.CaptureAndSendScreenshot", ex.Message, ex);
            }
        }

        private static (byte[] rgb, int width, int height) BitmapToRgb(Bitmap bmp)
        {
            int width  = bmp.Width;
            int height = bmp.Height;
            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride   = bmpData.Stride;
                int rowBytes = Math.Abs(stride);
                var bgra     = new byte[rowBytes * height];
                Marshal.Copy(bmpData.Scan0, bgra, 0, bgra.Length);

                var rgb = new byte[width * height * 3];
                for (int y = 0; y < height; y++)
                {
                    int srcRow  = stride > 0 ? y * stride : (height - 1 - y) * rowBytes;
                    int destRow = y * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        int src  = srcRow  + x * 4;
                        int dest = destRow + x * 3;
                        rgb[dest + 0] = bgra[src + 2];
                        rgb[dest + 1] = bgra[src + 1];
                        rgb[dest + 2] = bgra[src + 0];
                    }
                }
                return (rgb, width, height);
            }
            finally { bmp.UnlockBits(bmpData); }
        }

        // ---------------------------------------------------------------------------
        // Steam コールバック → JS イベント転送
        // ---------------------------------------------------------------------------

        private void RegisterSteamCallbacks()
        {
            SteamScreenshots.OnScreenshotRequested += () =>
            {
                if (_webView.IsDisposed || !_webView.IsHandleCreated) return;
                _webView.BeginInvoke(new Action(async () => { await CaptureAndSendScreenshotAsync(); }));
            };

            SteamUserStats.OnAchievementProgress += (name, current, max) =>
                PostEventToJs("OnAchievementProgress", new
                {
                    achievementName = name,
                    currentProgress = current,
                    maxProgress     = max,
                });

            SteamFriends.OnGameOverlayActivated += active =>
                PostEventToJs("OnGameOverlayActivated", new { active });

            SteamUser.OnMicroTxnAuthorizationResponse += (appId, orderId, authorized) =>
                PostEventToJs("OnMicroTxnAuthorizationResponse", new
                {
                    appId,
                    orderId    = orderId.ToString(),
                    authorized,
                });
        }

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _callbackTimer.Stop();
            _callbackTimer.Dispose();
            DisposeHandles();
            try { SteamClient.Shutdown(); }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "SteamBridgeImpl.Dispose", "SteamClient.Shutdown 失敗", ex);
            }
        }
    }
}
