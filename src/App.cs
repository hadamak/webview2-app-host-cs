using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    internal sealed class App : Form
    {
        private readonly WebView2         _webView  = new WebView2();
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly ZipContentProvider _zip;
        private readonly AppConfig        _config;
        private readonly string           _initialUri;
        private readonly bool             _disposeZipOnClose;
        private readonly PopupWindowOptions? _popupWindowOptions;

        // visibilitychange 用
        private bool _isMinimized = false;

        // 終了シーケンス管理
        // _closeNavExpected : Navigate("about:blank") 発行済み、NavigationStarting 待ち
        // _closeNavId       : NavigationStarting 受信後の NavigationId（0 = なし）
        // _closeConfirmed   : about:blank 到達確認済み → Close() 発行可
        private bool  _closeNavExpected = false;
        private ulong _closeNavId       = 0;
        private bool  _closeConfirmed   = false;

        // フルスクリーン用
        private bool             _isFullscreen    = false;
        private FormBorderStyle  _prevBorderStyle = FormBorderStyle.Sizable;
        private FormWindowState  _prevWindowState = FormWindowState.Normal;

        // favicon 管理（前のアイコンを Dispose するために保持）
        private Icon? _favicon = null;

        // Steam ブリッジ（steam_bridge.dll が存在する場合のみ非 null）
        private SteamBridge? _steamBridge;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        public App(
            ZipContentProvider zip,
            AppConfig config,
            string initialUri = "https://app.local/index.html",
            bool disposeZipOnClose = false,
            PopupWindowOptions? popupWindowOptions = null)
        {
            _zip               = zip;
            _config            = config;
            _initialUri        = initialUri;
            _disposeZipOnClose = disposeZipOnClose;
            _popupWindowOptions = popupWindowOptions;

            // ウィンドウ基本設定
            Text            = config.Title;
            ClientSize      = new Size(_popupWindowOptions?.Width ?? config.Width, _popupWindowOptions?.Height ?? config.Height);
            StartPosition   = (_popupWindowOptions?.HasPosition == true) ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
            if (_popupWindowOptions?.HasPosition == true)
                Location = new Point(_popupWindowOptions.Left, _popupWindowOptions.Top);

            // アイコン（EXE に埋め込まれたアイコンをそのまま使う）
            Icon = IconUtils.GetAppIcon();

            // WebView2 をフォームいっぱいに配置
            _webView.Dock = DockStyle.Fill;
            Controls.Add(_webView);
        }

        // ---------------------------------------------------------------------------
        // 初期化
        // ---------------------------------------------------------------------------

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                await InitWebViewAsync();
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "App.OnLoad", ex.Message, ex);
                MessageBox.Show(
                    $"WebView2 の初期化に失敗しました。\n\n{ex.Message}\n\n" +
                    "WebView2 ランタイムがインストールされているか確認してください。\n" +
                    "https://developer.microsoft.com/microsoft-edge/webview2/",
                    "初期化エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        private async System.Threading.Tasks.Task InitWebViewAsync()
        {
            // ユーザーデータディレクトリを EXE 名から自動決定
            // 例: MyApp.exe → %LOCALAPPDATA%\MyApp\WebView2\
            var exeName  = Path.GetFileNameWithoutExtension(
                System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
            var dataDir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                exeName, "WebView2");

            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _webView.EnsureCoreWebView2Async(env);

            var wv = _webView.CoreWebView2;

            // WebView2 設定
#if DEBUG
            wv.Settings.AreDevToolsEnabled = true;
#else
            wv.Settings.AreDevToolsEnabled = false;
#endif

            // カスタムスキームの登録（https://app.local/*）
            RegisterCustomScheme();

            // <title> 変化をウィンドウタイトルに反映
            wv.DocumentTitleChanged += (s, _) =>
            {
                if (!string.IsNullOrEmpty(wv.DocumentTitle))
                    Text = wv.DocumentTitle;
            };

            // favicon 追従
            SetupFaviconTracking();

            // JS からのウィンドウクローズ要求 (window.close())
            wv.WindowCloseRequested += (s, _) =>
            {
                _closeConfirmed = true;
                Close();
            };

            // NavigationStarting で終了ナビゲーションの ID を確定する。
            wv.NavigationStarting += (s, e) =>
            {
                if (_closeNavExpected && e.Uri == "about:blank")
                {
                    _closeNavExpected = false;
                    _closeNavId = e.NavigationId;
                }
                HandleNavigation(e.Uri, () => e.Cancel = true);
            };

            // NavigationId 照合で終了ナビゲーションの完了のみを処理する。
            wv.NavigationCompleted += (s, e) =>
            {
                if (_closeNavId == 0 || e.NavigationId != _closeNavId) return;
                _closeNavId = 0;
                if (e.IsSuccess && wv.Source == "about:blank")
                {
                    _closeConfirmed = true;
                    Close();
                }
            };

            // 外部リンク・ポップアップのハンドリング
            wv.NewWindowRequested += (s, e) => HandleNewWindowRequest(e);

            // 通知権限ハンドリング
            wv.PermissionRequested += (s, e) =>
            {
                if (e.PermissionKind != CoreWebView2PermissionKind.Notifications) return;

                var deferral = e.GetDeferral();
                try
                {
                    var result = MessageBox.Show(
                        $"{e.Uri} が通知の送信を求めています。\n\n許可しますか？",
                        "通知の許可",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    e.State = result == DialogResult.Yes
                        ? CoreWebView2PermissionState.Allow
                        : CoreWebView2PermissionState.Deny;
                    e.SavesInProfile = true;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            // visibilitychange 用スクリプト注入
            await wv.AddScriptToExecuteOnDocumentCreatedAsync(@"
                const __wv2VisibilityDeduper = {
                    lastState: document.visibilityState,
                    lastTick: 0
                };

                document.addEventListener('visibilitychange', function(event) {
                    const now = Date.now();
                    const state = document.visibilityState;

                    if (state === __wv2VisibilityDeduper.lastState &&
                        (now - __wv2VisibilityDeduper.lastTick) < 250) {
                        event.stopImmediatePropagation();
                        event.stopPropagation();
                        return;
                    }

                    __wv2VisibilityDeduper.lastState = state;
                    __wv2VisibilityDeduper.lastTick = now;
                }, true);

                window.chrome.webview.addEventListener('message', function(e) {
                    let data;
                    try { data = JSON.parse(e.data); } catch { return; }

                    if (data.event === 'visibilityChange') {
                        const desiredState = data.state === 'hidden' ? 'hidden' : 'visible';
                        const desiredHidden = desiredState === 'hidden';

                        if (document.visibilityState === desiredState && document.hidden === desiredHidden) {
                            return;
                        }

                        Object.defineProperty(document, 'visibilityState', {
                            value: desiredState, writable: true, configurable: true
                        });
                        Object.defineProperty(document, 'hidden', {
                            value: desiredHidden, writable: true, configurable: true
                        });
                        document.dispatchEvent(new Event('visibilitychange'));
                    }
                });
            ");

            // フルスクリーン状態の同期
            wv.ContainsFullScreenElementChanged += (s, e) =>
            {
                if (wv.ContainsFullScreenElement)
                {
                    if (!_isFullscreen) RequestFullscreen();
                }
                else
                {
                    if (_isFullscreen) ExitFullscreen();
                }
            };

            // ---------------------------------------------------------------------------
            // Steam ブリッジの初期化（メインウィンドウのみ。ポップアップは不要）
            // ---------------------------------------------------------------------------
            if (_popupWindowOptions == null)
            {
                TryInitSteam();
            }

            // index.html へナビゲート
            wv.Navigate(_initialUri);

            // 起動時フルスクリーン
            if (_config.Fullscreen)
                RequestFullscreen();
        }

        // ---------------------------------------------------------------------------
        // Steam ブリッジ初期化
        // ---------------------------------------------------------------------------

        private void TryInitSteam()
        {
            _steamBridge = SteamBridge.TryCreate(
                _webView,
                _config.SteamAppId,
                _config.SteamDevMode);

            if (_steamBridge == null) return;

            // JS からの Steam メッセージを受信して DLL へ転送
            _webView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                try
                {
                    _steamBridge?.HandleWebMessage(e.TryGetWebMessageAsString());
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "App.SteamWebMessageReceived", ex.Message);
                }
            };

#if DEBUG
            AppLog.Log("INFO", "App.TryInitSteam", "Steam ブリッジを有効化しました");
#endif
        }

        // ---------------------------------------------------------------------------
        // カスタムスキーム（https://app.local/ → ZIP からオンデマンド配信）
        // ---------------------------------------------------------------------------

        private void RegisterCustomScheme()
        {
            _webView.CoreWebView2.AddWebResourceRequestedFilter(
                "https://app.local/*",
                CoreWebView2WebResourceContext.All);

            foreach (var origin in _config.ProxyOrigins ?? Array.Empty<string>())
            {
                var normalized = origin.TrimEnd('/');
                _webView.CoreWebView2.AddWebResourceRequestedFilter(
                    normalized + "/*",
                    CoreWebView2WebResourceContext.All);
            }

            _webView.CoreWebView2.WebResourceRequested += (s, e) =>
                HandleWebResourceRequest(e);
        }

        private void HandleWebResourceRequest(CoreWebView2WebResourceRequestedEventArgs e)
        {
            var uri  = new Uri(e.Request.Uri);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);

            if (_config.IsProxyAllowed(uri))
            {
                HandleProxyRequest(e, uri);
                return;
            }

            var rangeHeader = e.Request.Headers.Contains("Range")
                ? e.Request.Headers.GetHeader("Range")
                : null;

            var stream = _zip.OpenEntry(path);
            if (stream == null)
            {
                e.Response = _webView.CoreWebView2.Environment
                    .CreateWebResourceResponse(null, 404, "Not Found",
                        "Content-Type: text/plain");
                return;
            }

            try
            {
                var mime  = MimeTypes.FromPath(path);
                var total = stream.Length;

                if (!string.IsNullOrEmpty(rangeHeader) && stream.CanSeek)
                {
                    var range = WebResourceHandler.ParseRange(rangeHeader!, total);
                    if (range == null)
                    {
                        stream.Dispose();
                        e.Response = _webView.CoreWebView2.Environment
                            .CreateWebResourceResponse(null, 416, "Range Not Satisfiable",
                                WebResourceHandler.BuildRangeNotSatisfiableHeaders(total));
                        return;
                    }

                    var (start, end) = range.Value;
                    var length = end - start + 1;

                    stream.Seek(start, SeekOrigin.Begin);
                    var rangeStream = new SubStream(stream, start, length);
                    try
                    {
                        e.Response = _webView.CoreWebView2.Environment
                            .CreateWebResourceResponse(rangeStream, 206, "Partial Content",
                                WebResourceHandler.BuildPartialResponseHeaders(mime, start, end, total));
                    }
                    catch
                    {
                        rangeStream.Dispose();
                        throw;
                    }
                }
                else
                {
                    e.Response = _webView.CoreWebView2.Environment
                        .CreateWebResourceResponse(stream, 200, "OK",
                            WebResourceHandler.BuildFullResponseHeaders(mime, total));
                }
            }
            catch (Exception ex)
            {
                stream.Dispose();
                AppLog.Log("ERROR", "App.HandleWebResourceRequest", ex.Message, ex);
                throw;
            }
        }

        private async void HandleProxyRequest(CoreWebView2WebResourceRequestedEventArgs e, Uri targetUri)
        {
            try
            {
                var deferral = e.GetDeferral();
                try
                {
                    var method  = new HttpMethod(e.Request.Method ?? "GET");
                    var request = new HttpRequestMessage(method, targetUri);

                    foreach (var header in e.Request.Headers)
                    {
                        var name = header.Key;
                        if (name.Equals("Host",    StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Origin",  StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { request.Headers.TryAddWithoutValidation(name, header.Value); } catch { }
                    }

                    var response = await _httpClient.SendAsync(
                        request, HttpCompletionOption.ResponseContentRead);

                    var body = await response.Content.ReadAsByteArrayAsync();
                    var ms   = new System.IO.MemoryStream(body);

                    var contentType = response.Content.Headers.ContentType?.ToString()
                        ?? "application/octet-stream";
                    var headers = $"Content-Type: {contentType}\r\n"
                        + $"Content-Length: {body.Length}\r\n"
                        + "Access-Control-Allow-Origin: *\r\n"
                        + "Cache-Control: no-store";

                    e.Response = _webView.CoreWebView2.Environment
                        .CreateWebResourceResponse(
                            ms,
                            (int)response.StatusCode,
                            response.ReasonPhrase ?? "OK",
                            headers);
                }
                finally
                {
                    deferral.Complete();
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "App.HandleProxyRequest", $"プロキシ転送失敗: {targetUri.AbsoluteUri}", ex);
                e.Response = _webView.CoreWebView2.Environment
                    .CreateWebResourceResponse(null, 502, "Bad Gateway", "Content-Type: text/plain");
            }
        }

        // ---------------------------------------------------------------------------
        // フルスクリーン
        // ---------------------------------------------------------------------------

        private void RequestFullscreen()
        {
            if (_isFullscreen) return;
            _prevBorderStyle = FormBorderStyle;
            _prevWindowState = WindowState;
            FormBorderStyle  = FormBorderStyle.None;
            WindowState      = FormWindowState.Maximized;
            _isFullscreen    = true;
        }

        private void ExitFullscreen()
        {
            if (!_isFullscreen) return;
            FormBorderStyle = _prevBorderStyle;
            WindowState     = _prevWindowState;
            _isFullscreen   = false;
        }

        // ---------------------------------------------------------------------------
        // favicon 追従
        // ---------------------------------------------------------------------------

        private void SetupFaviconTracking()
        {
            _webView.CoreWebView2.FaviconChanged += async (s, _) =>
            {
                var wv = _webView.CoreWebView2;

                AppLog.Log("INFO", "App.SetupFaviconTracking", $"FaviconChanged 発生, URI='{wv.FaviconUri}'");

                if (string.IsNullOrEmpty(wv.FaviconUri))
                {
                    if (!IsDisposed && IsHandleCreated)
                        Invoke(new Action(() =>
                        {
                            if (!IsDisposed) Icon = IconUtils.GetAppIcon();
                        }));
                    return;
                }

                try
                {
                    using var comStream = await wv.GetFaviconAsync(
                        CoreWebView2FaviconImageFormat.Png);

                    if (comStream == null) return;

                    using var pngMs = new System.IO.MemoryStream();
                    comStream.CopyTo(pngMs);
                    pngMs.Position = 0;

                    using var bmp     = new Bitmap(pngMs);
                    using var resized = new Bitmap(bmp, new Size(32, 32));

                    using var icoMs = new System.IO.MemoryStream();
                    IconUtils.WriteIco(resized, icoMs);
                    icoMs.Position = 0;
                    var newIcon = new Icon(icoMs);

                    if (!IsDisposed && IsHandleCreated)
                        Invoke(new Action(() =>
                        {
                            try
                            {
                                if (IsDisposed) return;
                                var oldIcon = _favicon;
                                _favicon = newIcon;
                                Icon     = newIcon;
                                oldIcon?.Dispose();
                                AppLog.Log("INFO", "App.SetupFaviconTracking", "適用完了");
                            }
                            catch (Exception invokeEx)
                            {
                                AppLog.Log("ERROR", "App.SetupFaviconTracking", "アイコン適用失敗", invokeEx);
                            }
                        }));
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "App.SetupFaviconTracking", "favicon の取得・変換に失敗", ex);
                }
            };
        }

        // ---------------------------------------------------------------------------
        // ナビゲーション・ポップアップ処理
        // ---------------------------------------------------------------------------

        private void OpenInDefaultBrowser(string uri)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = uri,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { AppLog.Log("ERROR", "App.OpenInDefaultBrowser", $"ブラウザで開けませんでした: {uri}", ex); }
        }

        private void OpenHostPopup(string uri, PopupWindowOptions popupOptions)
        {
            ZipContentProvider? popupZip = null;
            try
            {
                popupZip = new ZipContentProvider();
                if (!popupZip.Load())
                {
                    popupZip.Dispose();
                    OpenInDefaultBrowser(uri);
                    return;
                }

                var popup = new App(
                    popupZip,
                    _config,
                    initialUri: uri,
                    disposeZipOnClose: true,
                    popupWindowOptions: popupOptions);
                popup.Show();
                popupZip = null;
            }
            catch (Exception ex)
            {
                popupZip?.Dispose();
                AppLog.Log("ERROR", "App.OpenHostPopup", $"ホスト内ポップアップを開けませんでした: {uri}", ex);
                OpenInDefaultBrowser(uri);
            }
        }

        private void HandleNewWindowRequest(CoreWebView2NewWindowRequestedEventArgs e)
        {
            var uri = string.IsNullOrEmpty(e.Uri) ? "about:blank" : e.Uri;
            var popupOptions = PopupWindowOptions.FromRequestedFeatures(
                hasPosition: e.WindowFeatures.HasPosition,
                left: e.WindowFeatures.Left,
                top: e.WindowFeatures.Top,
                hasSize: e.WindowFeatures.HasSize,
                width: e.WindowFeatures.Width,
                height: e.WindowFeatures.Height,
                shouldDisplayMenuBar: e.WindowFeatures.ShouldDisplayMenuBar,
                shouldDisplayStatus: e.WindowFeatures.ShouldDisplayStatus,
                shouldDisplayToolbar: e.WindowFeatures.ShouldDisplayToolbar,
                shouldDisplayScrollBars: e.WindowFeatures.ShouldDisplayScrollBars,
                fallbackWidth: _config.Width,
                fallbackHeight: _config.Height);

            switch (NavigationPolicy.Classify(uri))
            {
                case NavigationPolicy.Action.OpenExternal:
                    e.Handled = true;
                    OpenInDefaultBrowser(uri);
                    break;
                case NavigationPolicy.Action.Allow:
                    if (NavigationPolicy.ShouldOpenHostPopup(uri))
                    {
                        e.Handled = true;
                        OpenHostPopup(uri, popupOptions);
                    }
                    break;
                default:
                    break;
            }
        }

        private void HandleNavigation(string uri, Action cancelAction)
        {
            switch (NavigationPolicy.Classify(uri))
            {
                case NavigationPolicy.Action.OpenExternal:
                    cancelAction();
                    OpenInDefaultBrowser(uri);
                    break;
                case NavigationPolicy.Action.Allow:
                default:
                    break;
            }
        }

        // ---------------------------------------------------------------------------
        // ウィンドウメッセージ処理
        // ---------------------------------------------------------------------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            var minimized = WindowState == FormWindowState.Minimized;
            if (minimized == _isMinimized) return;
            _isMinimized = minimized;

            if (_webView.CoreWebView2 == null) return;

            var state = minimized ? "hidden" : "visible";
            _webView.CoreWebView2.PostWebMessageAsString(
                $"{{\"event\":\"visibilityChange\",\"state\":\"{state}\"}}");
        }

        // ---------------------------------------------------------------------------
        // 終了ナビゲーション（beforeunload 発火）
        // ---------------------------------------------------------------------------

        private void StartCloseNavigation()
        {
            _closeNavExpected = true;
            _closeNavId       = 0;
            _webView.CoreWebView2.Navigate("about:blank");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_closeConfirmed || _webView.CoreWebView2 == null)
            {
                base.OnFormClosing(e);
                return;
            }

            e.Cancel = true;

            if (_closeNavExpected || _closeNavId != 0)
                return;

            StartCloseNavigation();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _favicon?.Dispose();
                _steamBridge?.Dispose();
                if (_disposeZipOnClose)
                    _zip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
