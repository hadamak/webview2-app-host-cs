using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// ホスト本体の操作機能（WebView2 依存の低レイヤー処理など）をバスに公開するコネクター。
    /// </summary>
    public sealed class InternalConnector : ReflectionDispatcherBase, IConnector
    {
        private readonly WebView2 _webView;
        private Action<string>? _publish;

        public InternalConnector(WebView2 webView)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
            _postMessage = msg => _publish?.Invoke(msg);
        }

        public string Name => "Internal";

        public Action<string> Publish
        {
            set => _publish = value;
        }

        public void Deliver(string messageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(messageJson)) return;
            if (IsForMe(messageJson)) HandleWebMessageCore(messageJson);
        }

        protected override string SourceName => "Internal";
        protected override bool ShouldWrapAsHandle(object result) => false;

        protected override Task<object?> ResolveTypeAsync(
            string? source, Dictionary<string, object>? p, string className, string methodName,
            object?[] argsRaw, object? id)
        {
            if (className == "Host") return Task.FromResult<object?>(this);
            return Task.FromResult<object?>(null);
        }

        /// <summary>
        /// WebView2 の画面をキャプチャし、RGB バイト配列とサイズ情報を返す。
        /// </summary>
        public async Task<object> CapturePreviewAsync()
        {
            if (_webView.IsDisposed || !_webView.IsHandleCreated)
                throw new InvalidOperationException("WebView2 が利用できません。");

            return await InvokeOnStaAsync(async () =>
            {
                if (_webView.CoreWebView2 == null)
                    throw new InvalidOperationException("CoreWebView2 が初期化されていません。");

                using (var ms = new MemoryStream())
                {
                    await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    ms.Position = 0;

                    using (var bmp = new Bitmap(ms))
                    {
                        var rgb = BitmapToRgb(bmp);
                        return (object)new
                        {
                            rgb = rgb, // byte[] で返す
                            width = bmp.Width,
                            height = bmp.Height
                        };
                    }
                }
            });
        }

        private static byte[] BitmapToRgb(Bitmap bmp)
        {
            int width = bmp.Width;
            int height = bmp.Height;

            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bmpData.Stride;
                int totalBytes = Math.Abs(stride) * height;
                var bgra = new byte[totalBytes];
                Marshal.Copy(bmpData.Scan0, bgra, 0, bgra.Length);

                var rgb = new byte[width * height * 3];
                for (int y = 0; y < height; y++)
                {
                    int srcRow = y * stride;
                    int destRow = y * width * 3;
                    for (int x = 0; x < width; x++)
                    {
                        int src = srcRow + x * 4;
                        int dest = destRow + x * 3;
                        // BGRA -> RGB
                        rgb[dest + 0] = bgra[src + 2];
                        rgb[dest + 1] = bgra[src + 1];
                        rgb[dest + 2] = bgra[src + 0];
                    }
                }
                return rgb;
            }
            finally { bmp.UnlockBits(bmpData); }
        }

        private async Task<T> InvokeOnStaAsync<T>(Func<Task<T>> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _webView.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (_disposed || _webView.CoreWebView2 == null)
                    { tcs.TrySetException(new InvalidOperationException("WebView2 が利用できません。")); return; }
                    tcs.TrySetResult(await action());
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }));
            return await tcs.Task;
        }

        private bool IsForMe(string json)
        {
            try
            {
                var dict = s_json.Deserialize<Dictionary<string, object>>(json);
                if (dict == null) return false;

                if (dict.TryGetValue("jsonrpc", out var jv) && string.Equals(jv?.ToString(), "2.0", StringComparison.OrdinalIgnoreCase))
                {
                    if (dict.TryGetValue("method", out var mv) && mv != null)
                    {
                        var methodStr = mv.ToString()!;
                        var dotIdx = methodStr.IndexOf('.');
                        if (dotIdx > 0)
                            return string.Equals(methodStr.Substring(0, dotIdx), SourceName, StringComparison.OrdinalIgnoreCase);
                    }
                }
                else if (dict.TryGetValue("source", out var sv))
                {
                    return string.Equals(sv?.ToString(), SourceName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            _disposed = true;
            DisposeHandles();
        }
    }
}
