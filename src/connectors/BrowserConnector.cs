using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// WebView2 と JS を双方向に繋ぐコネクター。
    /// 
    /// <para>
    /// また、自身も ReflectionDispatcherBase を継承しており、
    /// "Browser" エイリアスを通じてスクリーンショット等のブラウザ操作機能を JS/MCP に公開する。
    /// </para>
    /// </summary>
    public sealed class BrowserConnector : ReflectionDispatcherBase, IConnector, IBrowserTools
    {
        // -------------------------------------------------------------------
        // フィールド
        // -------------------------------------------------------------------

        private readonly WebView2  _webView;
        private Action<string>?   _publish;

        // -------------------------------------------------------------------
        // コンストラクタ
        // -------------------------------------------------------------------

        public BrowserConnector(WebView2 webView)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));

            // ReflectionDispatcherBase の送信口を設定
            _postMessage = msg => PostToWebView(msg);

            // WebMessageReceived: JS → バス
            _webView.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    var json = WebMessageHelper.GetJsonPayload(
                        () => e.TryGetWebMessageAsString(),
                        () => e.WebMessageAsJson);
                    
                    // 1. まず自分自身 (Browser) 宛かチェック
                    if (IsForMe(json))
                    {
                        HandleWebMessageCore(json);
                    }
                    else
                    {
                        // 2. 自分宛でなければバスに流す
                        _publish?.Invoke(json);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "BrowserConnector.WebMessageReceived", ex.Message, ex);
                }
            };
        }

        // -------------------------------------------------------------------
        // IConnector
        // -------------------------------------------------------------------

        public string Name => "Browser";

        public Action<string> Publish
        {
            set
            {
                _publish = value;
                // ReflectionDispatcherBase の送信口を「バスへの配信」と「WebView への転送」の両方に繋ぐ
                _postMessage = msg =>
                {
                    _publish?.Invoke(msg);
                    PostToWebView(msg);
                };
            }
        }

        public void Deliver(string messageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(messageJson)) return;

            // バスから回ってきたメッセージが自分 (Browser) 宛ならディスパッチする
            if (IsForMe(messageJson))
            {
                HandleWebMessageCore(messageJson);
            }
            else
            {
                // 自分宛でない（＝他のコネクターからの応答や通知）場合は JS に転送する
                PostToWebView(messageJson);
            }
        }

        // -------------------------------------------------------------------
        // ReflectionDispatcherBase 実装
        // -------------------------------------------------------------------

        protected override string SourceName => "Browser";

        protected override bool ShouldWrapAsHandle(object result) => false;

        protected override Task<object?> ResolveTypeAsync(
            string? source, Dictionary<string, object>? p, string className, string methodName,
            object?[] argsRaw, object? id)
        {
            // Browser コネクターは特定のクラス名（WebView）に対して、自分自身のインスタンスを返す。
            // これにより、インスタンスメソッドである NavigateAsync 等が呼び出し可能になる。
            if (className == "WebView")
            {
                return Task.FromResult<object?>(this);
            }
            return Task.FromResult<object?>(null);
        }

        // -------------------------------------------------------------------
        // 公開メソッド (JS / MCP / Reflection から呼ばれる)
        // -------------------------------------------------------------------

        /// <summary>現在の画面をキャプチャする。outputPath があればファイル保存し、なければ Base64 を返す。</summary>
        public async Task<object> ScreenshotAsync(string? outputPath = null)
        {
            return await InvokeOnStaAsync(async () =>
            {
                EnsureReady();

                // パスの解決
                bool saveToFile = !string.IsNullOrEmpty(outputPath);
                if (saveToFile)
                {
                    if (!Path.IsPathRooted(outputPath))
                        outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, outputPath!);

                    var dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }

                using (var ms = new MemoryStream())
                {
                    await _webView.CoreWebView2
                        .CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms)
                        .ConfigureAwait(false);

                    var bytes = ms.ToArray();
                    int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                    int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];

                    if (saveToFile)
                    {
                        File.WriteAllBytes(outputPath!, bytes);
                        return (object)new { path = outputPath, width = w, height = h };
                    }
                    else
                    {
                        return (object)new { base64 = Convert.ToBase64String(bytes), width = w, height = h };
                    }
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>IBrowserTools 互換用 (MCP 等から呼ばれる)</summary>
        async Task<(string Base64, int Width, int Height)> IBrowserTools.ScreenshotAsync(CancellationToken ct)
        {
            var res = await ScreenshotAsync(null);
            // 匿名型を辞書に変換して展開
            var dict = s_json.Deserialize<Dictionary<string, object>>(s_json.Serialize(res));
            return (dict["base64"].ToString(), (int)dict["width"], (int)dict["height"]);
        }

        public Task<string> EvaluateAsync(string script, CancellationToken ct = default)
            => InvokeOnStaAsync(async () =>
            {
                EnsureReady();
                return await _webView.CoreWebView2
                    .ExecuteScriptAsync(script).ConfigureAwait(false);
            }, ct);

        public Task<string> GetUrlAsync(CancellationToken ct = default)
            => InvokeOnStaAsync(() => Task.FromResult(_webView.CoreWebView2.Source), ct);

        public Task NavigateAsync(string url, CancellationToken ct = default)
            => InvokeOnStaAsync(async () =>
            {
                EnsureReady();
                var wv  = _webView.CoreWebView2;
                var tcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                EventHandler<CoreWebView2NavigationCompletedEventArgs>? h = null;
                h = (_, e) =>
                {
                    wv.NavigationCompleted -= h;
                    if (e.IsSuccess)
                    {
                        tcs.TrySetResult(true);
                    }
                    else if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                    {
                        // 操作がキャンセルされた（リダイレクトや二重遷移など）場合は、
                        // 致命的なエラーとはみなさず完了させる
                        AppLog.Log("WARN", "BrowserConnector.NavigateAsync", "ナビゲーションがキャンセルされました (OperationCanceled)");
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        tcs.TrySetException(new Exception($"ナビゲーション失敗: {e.WebErrorStatus}"));
                    }
                };
                wv.NavigationCompleted += h;
                wv.Navigate(url);

                using (var reg = ct.Register(() => { wv.NavigationCompleted -= h; tcs.TrySetCanceled(); }))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            }, ct);

        public async Task<string> GetContentAsync(CancellationToken ct = default)
        {
            var json = await EvaluateAsync("document.documentElement.outerHTML", ct)
                .ConfigureAwait(false);
            if (json.Length >= 2 && json[0] == '"' && json[json.Length - 1] == '"')
                json = System.Text.RegularExpressions.Regex.Unescape(
                    json.Substring(1, json.Length - 2));
            return json;
        }

        // -------------------------------------------------------------------
        // STA マーシャリング
        // -------------------------------------------------------------------

        private Task<T> InvokeOnStaAsync<T>(Func<Task<T>> action, CancellationToken ct)
        {
            if (_webView.IsDisposed || !_webView.IsHandleCreated)
                return Task.FromException<T>(
                    new InvalidOperationException("WebView2 が利用できません。"));

            var tcs = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (var reg = ct.Register(() => tcs.TrySetCanceled()))
            {
                _webView.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        if (_disposed || _webView.CoreWebView2 == null)
                        { tcs.TrySetException(new InvalidOperationException("WebView2 が利用できません。")); return; }
                        tcs.TrySetResult(await action().ConfigureAwait(false));
                    }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                }));
                return tcs.Task;
            }
        }

        private async Task InvokeOnStaAsync(Func<Task> action, CancellationToken ct)
            => await InvokeOnStaAsync<int>(async () => { await action(); return 0; }, ct)
                .ConfigureAwait(false);

        private void EnsureReady()
        {
            if (_webView.CoreWebView2 == null)
                throw new InvalidOperationException("CoreWebView2 が初期化されていません。");
        }

        private void PostToWebView(string payload)
        {
            if (_disposed || _webView.IsDisposed || !_webView.IsHandleCreated) return;

            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null) return;
                try
                {
                    _webView.CoreWebView2.PostWebMessageAsString(payload);
                }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", "BrowserConnector.PostToWebView", ex.Message, ex);
                }
            }));
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

        // -------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------

        public void Dispose()
        {
            _disposed = true;
            DisposeHandles();
        }
    }
}