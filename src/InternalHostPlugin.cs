using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// ホストアプリ本体の機能を JS に公開する組み込みプラグイン。
    ///
    /// 外部 DLL では実行できない「WebView2 コントロールそのものへのアクセス」を
    /// 他のプラグインと同じ 3 階層プロトコル（Host.Internal.ClassName.MethodName）で
    /// JS から呼び出せるようにする。
    ///
    /// className はホスト機能の論理カテゴリとして扱う（外部 DLL のようにリフレクションで
    /// 実型を解決するのではなく、switch でカテゴリ → メソッドへディスパッチする）。
    ///
    /// 現在のカテゴリ:
    ///   WebView  … WebView2 コントロールの操作
    ///              - CapturePreview() → { rgb: number[], width: number, height: number }
    ///
    /// 追加の作法:
    ///   1. 新カテゴリなら DispatchClassName に case を追加し、専用メソッドへ委譲する。
    ///   2. 既存カテゴリへのメソッド追加なら、対応する Dispatch*MethodName に case を追加する。
    /// </summary>
    internal sealed class InternalHostPlugin : IHostPlugin
    {
        // ---------------------------------------------------------------------------
        // フィールド
        // ---------------------------------------------------------------------------

        private readonly WebView2             _webView;
        private readonly JavaScriptSerializer _jss = new JavaScriptSerializer();
        private bool _disposed;

        // ---------------------------------------------------------------------------
        // IHostPlugin
        // ---------------------------------------------------------------------------

        public string PluginName => "Internal";

        public InternalHostPlugin(WebView2 webView)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        }

        /// <summary>app.conf.json の設定は不要のため何もしない。</summary>
        public void Initialize(string configJson) { }

        // ---------------------------------------------------------------------------
        // JS → C# メッセージ受信
        // ---------------------------------------------------------------------------

        public void HandleWebMessage(string webMessageJson)
        {
            if (_disposed) return;
            try
            {
                var msg = _jss.Deserialize<Dictionary<string, object>>(webMessageJson);
                if (msg == null) return;

                if (!(msg.TryGetValue("source", out var src) &&
                      string.Equals(src as string, "Internal", StringComparison.OrdinalIgnoreCase)))
                    return;

                if (!(msg.TryGetValue("messageId", out var mid) && mid as string == "invoke"))
                    return;

                var asyncId = msg.TryGetValue("asyncId", out var aid) && aid != null
                    ? Convert.ToDouble(aid) : -1.0;

                var p = msg.TryGetValue("params", out var pv)
                    ? pv as Dictionary<string, object> : null;

                var className  = p != null && p.TryGetValue("className",  out var cn) ? cn as string : null;
                var methodName = p != null && p.TryGetValue("methodName", out var mn) ? mn as string : null;

                if (string.IsNullOrEmpty(className))
                {
                    SendError(asyncId, "className が指定されていません。");
                    return;
                }
                if (string.IsNullOrEmpty(methodName))
                {
                    SendError(asyncId, "methodName が指定されていません。");
                    return;
                }

                DispatchClassName(className!, methodName!, asyncId);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "InternalHostPlugin.HandleWebMessage", ex.Message, ex);
            }
        }

        // ---------------------------------------------------------------------------
        // 第1段ディスパッチ: className → カテゴリ
        // ---------------------------------------------------------------------------

        private void DispatchClassName(string className, string methodName, double asyncId)
        {
            switch (className)
            {
                case "WebView":
                    DispatchWebView(methodName, asyncId);
                    break;

                // 新カテゴリはここに case を追加する
                // case "Window":
                //     DispatchWindow(methodName, asyncId);
                //     break;

                default:
                    SendError(asyncId, $"未知のクラス名: {className}");
                    break;
            }
        }

        // ---------------------------------------------------------------------------
        // 第2段ディスパッチ: WebView カテゴリ
        // ---------------------------------------------------------------------------

        private void DispatchWebView(string methodName, double asyncId)
        {
            switch (methodName)
            {
                case "CapturePreview":
                    Task.Run(async () => await CapturePreviewAsync(asyncId));
                    break;

                default:
                    SendError(asyncId, $"未知のメソッド名: WebView.{methodName}");
                    break;
            }
        }

        // ---------------------------------------------------------------------------
        // WebView.CapturePreview
        // ---------------------------------------------------------------------------

        /// <summary>
        /// WebView2 の現在の表示内容を PNG でキャプチャし、RGB バイト配列に変換して JS に返す。
        ///
        /// JS 側の戻り値: { rgb: number[], width: number, height: number }
        /// （JS 側では new Uint8Array(result.rgb) として利用可能）
        ///
        /// CoreWebView2 API は UI スレッドで呼ぶ必要があるため BeginInvoke で移譲し、
        /// TaskCompletionSource で非同期完了を待つ。
        /// </summary>
        private async Task CapturePreviewAsync(double asyncId)
        {
            var tcs = new TaskCompletionSource<(byte[] rgb, int width, int height)>();

            _webView.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (_disposed || _webView.CoreWebView2 == null)
                    {
                        tcs.SetException(new InvalidOperationException("WebView2 が利用できません。"));
                        return;
                    }

                    using var pngStream = new MemoryStream();
                    await _webView.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, pngStream);
                    pngStream.Position = 0;

                    using var bmp = new Bitmap(pngStream);
                    tcs.SetResult(BitmapToRgb(bmp));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }));

            try
            {
                var (rgb, width, height) = await tcs.Task.ConfigureAwait(false);

#if DEBUG
                AppLog.Log("INFO", "InternalHostPlugin.WebView.CapturePreview",
                    $"キャプチャ完了 ({width}x{height}, {rgb.Length} bytes)");
#endif
                // JavaScriptSerializer は byte[] を Base64 に変換するため手動で構築する。
                // JS 側では new Uint8Array(result.rgb) として使用できる。
                var resultJson =
                    $"{{\"rgb\":[{string.Join(",", rgb)}]," +
                    $"\"width\":{width}," +
                    $"\"height\":{height}}}";

                SendResult(asyncId, resultJson);
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException tie
                    ? tie.InnerException ?? ex : ex;
                AppLog.Log("ERROR", "InternalHostPlugin.WebView.CapturePreview", inner.Message, inner);
                SendError(asyncId, inner.Message);
            }
        }

        // ---------------------------------------------------------------------------
        // Bitmap → RGB 変換
        // ---------------------------------------------------------------------------

        private static (byte[] rgb, int width, int height) BitmapToRgb(Bitmap bmp)
        {
            int width  = bmp.Width;
            int height = bmp.Height;

            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
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
                        rgb[dest + 0] = bgra[src + 2]; // R
                        rgb[dest + 1] = bgra[src + 1]; // G
                        rgb[dest + 2] = bgra[src + 0]; // B
                    }
                }
                return (rgb, width, height);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        // ---------------------------------------------------------------------------
        // JS への送信ヘルパー
        // ---------------------------------------------------------------------------

        /// <summary>成功結果を JS に送信する。resultJson はシリアライズ済みの JSON 文字列。</summary>
        private void SendResult(double asyncId, string resultJson)
        {
            PostToWebView(
                $"{{\"source\":\"Internal\",\"messageId\":\"invoke-result\"," +
                $"\"result\":{resultJson}," +
                $"\"asyncId\":{FormatDouble(asyncId)}}}");
        }

        private void SendError(double asyncId, string errorMessage)
        {
            PostToWebView(
                $"{{\"source\":\"Internal\",\"messageId\":\"invoke-result\"," +
                $"\"error\":\"{EscapeJsonString(errorMessage)}\"," +
                $"\"asyncId\":{FormatDouble(asyncId)}}}");
        }

        private void PostToWebView(string payload)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null) return;
                try
                {
                    _webView.CoreWebView2.PostWebMessageAsString(payload);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "InternalHostPlugin.PostToWebView", ex.Message, ex);
                }
            }));
        }

        // ---------------------------------------------------------------------------
        // JSON ヘルパー
        // ---------------------------------------------------------------------------

        private static string EscapeJsonString(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"")
             .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

        private static string FormatDouble(double d) =>
            d.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            _disposed = true;
        }
    }
}