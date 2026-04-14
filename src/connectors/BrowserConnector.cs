using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// WebView2 の JavaScript ↔ C# メッセージングを担うコネクター。
    /// </summary>
    public sealed class BrowserConnector : ReflectionDispatcherBase, IConnector, IBrowserTools
    {
        private readonly WebView2  _webView;
        private Action<string>?   _publish;

        /// <summary>
        /// JS から <c>Host.Browser.WebView.ScreenshotAsync("path")</c> で呼ばれたときの戻り値型。
        /// </summary>
        public sealed class ScreenshotFileResult
        {
            /// <summary>保存先の絶対パス。</summary>
            public string path   { get; set; } = "";
            /// <summary>画像の幅（ピクセル）。</summary>
            public int    width  { get; set; }
            /// <summary>画像の高さ（ピクセル）。</summary>
            public int    height { get; set; }
        }

        public BrowserConnector(WebView2 webView)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
            _postMessage = msg => PostToWebView(msg);

            _webView.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    var json = WebMessageHelper.GetJsonPayload(() => e.TryGetWebMessageAsString(), () => e.WebMessageAsJson);
                    if (IsForMe(json)) HandleWebMessageCore(json);
                    else _publish?.Invoke(json);
                }
                catch (Exception ex) { AppLog.Log(AppLog.LogLevel.Error, "BrowserConnector.WebMessageReceived", ex.Message, ex); }
            };
        }

        public string Name => "Browser";
        public Action<string> Publish
        {
            set
            {
                _publish = value;
                _postMessage = msg =>
                {
                    _publish?.Invoke(msg);
                    PostToWebView(msg);
                };
            }
        }

        public void Deliver(string messageJson, Dictionary<string, object>? messageDict)
        {
            if (_disposed || string.IsNullOrWhiteSpace(messageJson)) return;
            if (IsForMe(messageJson, messageDict)) HandleWebMessageCore(messageJson, messageDict);
            else PostToWebView(messageJson);
        }

        protected override string SourceName => "Browser";
        protected override bool ShouldWrapAsHandle(object result) => false;

        protected override Task<object?> ResolveTypeAsync(string? s, Dictionary<string, object>? p, string c, string m, object?[] a, object? i)
        {
            if (c == "WebView" || c == "Host") return Task.FromResult<object?>(this);
            return Task.FromResult<object?>(null);
        }

        public Task<string> EvaluateAsync(string script, CancellationToken ct = default)
            => InvokeOnStaAsync<string>(async () => { EnsureReady(); return await _webView.CoreWebView2.ExecuteScriptAsync(script); }, ct);

        /// <summary>
        /// WebView2 の現在の表示内容を PNG としてキャプチャし、Base64 文字列とサイズを返す。
        /// </summary>
        public async Task<ScreenshotResult> ScreenshotAsync(CancellationToken ct = default)
        {
            return await InvokeOnStaAsync<ScreenshotResult>(async () =>
            {
                EnsureReady();
                await Task.Delay(50, ct);

                using (var ms = new MemoryStream())
                {
                    await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    ms.Position = 0;
                    using (var bmp = new Bitmap(ms))
                    {
                        return new ScreenshotResult
                        {
                            base64 = Convert.ToBase64String(ms.ToArray()),
                            width = bmp.Width,
                            height = bmp.Height
                        };
                    }
                }
            }, ct);
        }

        /// <summary>
        /// 指定パスに PNG を保存する。JS から呼ばれるオーバーロード。
        /// </summary>
        public async Task<ScreenshotFileResult> ScreenshotAsync(string outputPath)
        {
            return await InvokeOnStaAsync<ScreenshotFileResult>(async () =>
            {
                EnsureReady();
                await Task.Delay(50);

                using (var ms = new MemoryStream())
                {
                    await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    ms.Position = 0;

                    int width, height;
                    using (var bmp = new Bitmap(ms))
                    {
                        width  = bmp.Width;
                        height = bmp.Height;
                    }

                    var fullPath = Path.IsPathRooted(outputPath)
                        ? outputPath
                        : Path.Combine(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!) ?? ".", outputPath);

                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllBytes(fullPath, ms.ToArray());
                    AppLog.Log(AppLog.LogLevel.Info, "BrowserConnector.ScreenshotAsync", $"スクリーンショット保存: {AppLog.DescribePath(fullPath)} ({width}x{height})");

                    return new ScreenshotFileResult { path = fullPath, width = width, height = height };
                }
            }, default);
        }

        public Task NavigateAsync(string url, CancellationToken ct = default)
            => InvokeOnStaAsync(async () =>
            {
                EnsureReady();
                var wv = _webView.CoreWebView2;
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<CoreWebView2NavigationCompletedEventArgs> h = (s, e) =>
                {
                    if (e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) tcs.TrySetResult(true);
                    else tcs.TrySetException(new Exception($"Navigation failed: {e.WebErrorStatus}"));
                };
                try
                {
                    wv.NavigationCompleted += h;
                    wv.Navigate(url);
                    using (ct.Register(() => tcs.TrySetCanceled())) await tcs.Task;
                }
                finally { wv.NavigationCompleted -= h; }
            }, ct);

        public Task<string> GetUrlAsync(CancellationToken ct = default)
            => InvokeOnStaAsync<string>(() => Task.FromResult(_webView.CoreWebView2.Source), ct);

        public async Task<string> GetContentAsync(CancellationToken ct = default)
        {
            var json = await EvaluateAsync("document.documentElement.outerHTML", ct);
            return UnescapeJsString(json);
        }

        public Task ClickAsync(string selector, CancellationToken ct = default)
            => InvokeOnStaAsync(async () => {
                EnsureReady();
                var script = $@"
                    (function() {{
                        const el = document.querySelector({JsEncode(selector)});
                        if (!el) throw new Error('NotFound');
                        el.focus();
                        const opts = {{ bubbles: true, cancelable: true, view: window }};
                        el.dispatchEvent(new MouseEvent('mousedown', opts));
                        el.dispatchEvent(new MouseEvent('mouseup', opts));
                        el.click();
                        el.dispatchEvent(new MouseEvent('pointerdown', opts));
                        el.dispatchEvent(new MouseEvent('pointerup', opts));
                    }})()";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }, ct);

        public Task TypeAsync(string selector, string text, CancellationToken ct = default)
            => InvokeOnStaAsync(async () => {
                EnsureReady();
                var script = $@"
                    (function() {{
                        const el = document.querySelector({JsEncode(selector)});
                        if (!el) throw new Error('NotFound');
                        el.focus();
                        el.value = {JsEncode(text)};
                        el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        el.dispatchEvent(new Event('change', {{ bubbles: true }}));
                        el.dispatchEvent(new KeyboardEvent('keydown', {{ bubbles: true }}));
                        el.dispatchEvent(new KeyboardEvent('keyup', {{ bubbles: true }}));
                    }})()";
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }, ct);

        public Task ScrollAsync(int x, int y, CancellationToken ct = default)
            => InvokeOnStaAsync(async () => {
                EnsureReady();
                await _webView.CoreWebView2.ExecuteScriptAsync($"window.scrollTo({x},{y})");
            }, ct);

        public Task<string> PickFolderAsync(CancellationToken ct = default)
        {
            return InvokeOnStaAsync<string>(() =>
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "作業ディレクトリを選択してください";
                    dialog.ShowNewFolderButton = true;
                    var result = dialog.ShowDialog(_webView);
                    return Task.FromResult(result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : "");
                }
            }, ct);
        }

        private static string JsEncode(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

        private static string UnescapeJsString(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            if (json.Length >= 2 && json[0] == '"' && json[json.Length - 1] == '"')
            {
                try { return s_json.Deserialize<string>(json) ?? ""; }
                catch { return System.Text.RegularExpressions.Regex.Unescape(json.Substring(1, json.Length - 2)); }
            }
            return json;
        }

        private Task<T> InvokeOnStaAsync<T>(Func<Task<T>> action, CancellationToken ct)
        {
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return Task.FromException<T>(new InvalidOperationException("WebView2NotAvailable"));
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _webView.BeginInvoke(new Action(async () => {
                try {
                    using (ct.Register(() => tcs.TrySetCanceled())) {
                        if (_disposed || _webView.CoreWebView2 == null) throw new InvalidOperationException("WebView2NotAvailable");
                        tcs.TrySetResult(await action());
                    }
                }
                catch (OperationCanceledException) { tcs.TrySetCanceled(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }));
            return tcs.Task;
        }

        private Task InvokeOnStaAsync(Func<Task> action, CancellationToken ct)
        {
            return InvokeOnStaAsync<bool>(async () => { await action(); return true; }, ct);
        }

        private void EnsureReady() { if (_webView.CoreWebView2 == null) throw new InvalidOperationException("WebView2NotInitialized"); }

        private void PostToWebView(string payload)
        {
            if (_disposed || _webView.IsDisposed || !_webView.IsHandleCreated) return;
            _webView.BeginInvoke(new Action(() => {
                try { _webView.CoreWebView2?.PostWebMessageAsString(payload); } catch { }
            }));
        }

        private bool IsForMe(string json, Dictionary<string, object>? dict = null)
        {
            try {
                dict ??= s_json.Deserialize<Dictionary<string, object>>(json);
                if (dict == null) return false;
                var method = dict.ContainsKey("method") ? dict["method"]?.ToString() : null;
                if (method != null && method.StartsWith(SourceName + ".", StringComparison.OrdinalIgnoreCase)) return true;
                return dict.ContainsKey("source") && string.Equals(dict["source"]?.ToString(), SourceName, StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
        }

        public void Dispose() { _disposed = true; DisposeHandles(); }
    }
}
