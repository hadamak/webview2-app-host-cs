using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    internal sealed class App : Form
    {
        private readonly WebView2         _webView  = new WebView2();
        private readonly ZipContentProvider _zip;
        private readonly AppConfig        _config;
        private readonly string           _initialUri;
        private readonly bool             _disposeZipOnClose;
        private readonly PopupWindowOptions? _popupWindowOptions;

        // visibilitychange 用
        private bool _isMinimized = false;

        // 終了確認用
        private readonly CloseRequestState _closeState = new CloseRequestState();

        // フルスクリーン用
        private bool             _isFullscreen    = false;
        private FormBorderStyle  _prevBorderStyle = FormBorderStyle.Sizable;
        private FormWindowState  _prevWindowState = FormWindowState.Normal;

        // favicon 管理（前のアイコンを Dispose するために保持）
        private Icon? _favicon = null;

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
            wv.Settings.AreDefaultContextMenusEnabled = false;
            wv.Settings.IsZoomControlEnabled          = false;
#if DEBUG
            wv.Settings.IsStatusBarEnabled  = true;
            wv.Settings.AreDevToolsEnabled  = true;
#else
            wv.Settings.IsStatusBarEnabled  = false;
            wv.Settings.AreDevToolsEnabled  = false;
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
            // メインフレームの window.close() は AddScriptToExecuteOnDocumentCreated で
            // about:blank 遷移に差し替えてあるため、通常の操作でこのイベントが発火することはない。
            // （iframe の window.close() はその iframe 自身に作用するだけでここには届かない）
            // スクリプト注入前の極めて短い起動直後にページが window.close() を呼んだ場合など
            // 理論上ありうる抜け道への防御として、即 Close() せず about:blank 経由で
            // beforeunload を通す経路に統一している。
            wv.WindowCloseRequested += (s, _) =>
            {
                if (_closeState.IsClosingConfirmed || _closeState.IsHostCloseNavigationPending)
                    return;

                _closeState.BeginHostCloseNavigation();
                wv.Navigate("about:blank");
            };

            // 外部リンク・ナビゲーションのハンドリング
            wv.NewWindowRequested += (s, e) => HandleNewWindowRequest(e);
            wv.NavigationStarting += (s, e) => HandleNavigation(e.Uri, () => e.Cancel  = true);

            // 遷移完了時の判定（beforeunload を経て about:blank に到達した ＝ 終了承諾）
            wv.NavigationCompleted += (s, e) =>
            {
                if (_closeState.TryCompleteCloseNavigation(e.IsSuccess))
                {
                    Close();
                }
            };

            // C# から JS へのイベント通知の土台 & window.close() の標準化。
            // NOTE: AddScriptToExecuteOnDocumentCreated はメインフレームにのみ適用される。
            //       ただし iframe の window.close() は iframe 自身のブラウジングコンテキストに
            //       作用するだけで、トップレベルウィンドウの WindowCloseRequested は発火しない。
            //       クロスオリジン iframe からの window.close() がアプリ終了に直結する問題は
            //       実際には存在しない。
            await wv.AddScriptToExecuteOnDocumentCreatedAsync(@"
                // window.close() を about:blank への遷移に置き換える。
                // これによりセキュリティ制限を回避し、かつ beforeunload を確実に発火させる。
                const __wv2HostClose = function() {
                    location.href = 'about:blank';
                };
                const __wv2InstallCloseOverride = function(target, propertyName) {
                    if (!target) {
                        return;
                    }

                    try {
                        Object.defineProperty(target, propertyName, {
                            value: __wv2HostClose,
                            writable: false,
                            configurable: true
                        });
                    } catch {
                        try { target[propertyName] = __wv2HostClose; } catch {}
                    }
                };

                __wv2InstallCloseOverride(window, 'close');
                __wv2InstallCloseOverride(globalThis, 'close');
                try { __wv2InstallCloseOverride(Window.prototype, 'close'); } catch {}

                const __wv2VisibilityDeduper = {
                    lastState: document.visibilityState,
                    lastTick: 0
                };

                document.addEventListener('visibilitychange', function(event) {
                    const now = Date.now();
                    const state = document.visibilityState;

                    // WebView2 の標準イベントとホスト補完イベントが近接して二重発火する場合、
                    // 同一 state の後続イベントだけを capture 段階で止める。
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

                        // 既に同じ state に同期済みなら何もしない。
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

            // index.html へナビゲート
            wv.Navigate(_initialUri);

            // 起動時フルスクリーン
            if (_config.Fullscreen)
                RequestFullscreen();
        }

        // ---------------------------------------------------------------------------
        // カスタムスキーム（https://app.local/ → ZIP からオンデマンド配信）
        // ---------------------------------------------------------------------------

        private void RegisterCustomScheme()
        {
            _webView.CoreWebView2.AddWebResourceRequestedFilter(
                "https://app.local/*",
                CoreWebView2WebResourceContext.All);

            _webView.CoreWebView2.WebResourceRequested += (s, e) =>
                HandleWebResourceRequest(e);
        }

        private void HandleWebResourceRequest(CoreWebView2WebResourceRequestedEventArgs e)
        {
            var uri  = new Uri(e.Request.Uri);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);

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
                var total = stream.Length;   // ← ここより前で例外が起きたら catch に飛ぶ

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
                    // rangeStream がレスポンスの所有者となり、WebView2 により消費後 Dispose される。
                    // 失敗時は下の catch で rangeStream.Dispose() を呼び、内側の stream まで閉じる。
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
                    // 成功時は WebView2 がレスポンス消費後に stream を Dispose する。
                    // 失敗時（例外）のみ catch 側で明示的に Dispose する。
                    e.Response = _webView.CoreWebView2.Environment
                        .CreateWebResourceResponse(stream, 200, "OK",
                            WebResourceHandler.BuildFullResponseHeaders(mime, total));
                }
            }
            catch (Exception ex)
            {
                stream.Dispose();  // catch でのみ Dispose
                AppLog.Log("ERROR", "App.HandleWebResourceRequest", ex.Message, ex);
                throw;
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
                    Invoke(new Action(() =>
                    {
                        Icon = IconUtils.GetAppIcon();
                    }));
                    return;
                }

                try
                {
                    #if DEBUG
                    AppLog.Log("DEBUG", "App.SetupFaviconTracking", "GetFaviconAsync 呼び出し開始");
                    #endif
                    
                    using var comStream = await wv.GetFaviconAsync(
                        CoreWebView2FaviconImageFormat.Png);

                    if (comStream == null)
                    {
                        AppLog.Log("WARN", "App.SetupFaviconTracking", "comStream が null (WebView2 が画像を返さなかった)");
                        return;
                    }

                    #if DEBUG
                    AppLog.Log("DEBUG", "App.SetupFaviconTracking", "comStream 取得成功、PNGへコピー");
                    #endif

                    // COMStreamWrapper は Seek 不可なため MemoryStream に一度コピーする
                    using var pngMs = new System.IO.MemoryStream();
                    comStream.CopyTo(pngMs);
                    pngMs.Position = 0;

                    #if DEBUG
                    AppLog.Log("DEBUG", "App.SetupFaviconTracking", $"PNG コピー完了、サイズ={pngMs.Length}。Bitmap 変換開始");
                    #endif

                    using var bmp     = new Bitmap(pngMs);
                    using var resized = new Bitmap(bmp, new Size(32, 32));

                    #if DEBUG
                    AppLog.Log("DEBUG", "App.SetupFaviconTracking", "Bitmap リサイズ完了、ICO 変換開始");
                    #endif

                    // MemoryStream 経由で ICO フォーマットに変換して Icon を生成
                    using var icoMs = new System.IO.MemoryStream();
                    IconUtils.WriteIco(resized, icoMs);
                    icoMs.Position = 0;
                    var newIcon = new Icon(icoMs);

                    #if DEBUG
                    AppLog.Log("DEBUG", "App.SetupFaviconTracking", "ICO 変換成功、UIスレッド適用待ち");
                    #endif

                    Invoke(new Action(() =>
                    {
                        try
                        {
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
        // ウィンドウメッセージ処理
        // ---------------------------------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closeState.IsClosingConfirmed && _webView.CoreWebView2 != null)
            {
                e.Cancel = true;
                if (!_closeState.IsHostCloseNavigationPending)
                    _closeState.BeginHostCloseNavigation();
                // about:blank への遷移を試みることで beforeunload を発火させる。
                // ユーザーが承諾すれば遷移が完了し、NavigationCompleted でアプリを閉じる。
                _webView.CoreWebView2.Navigate("about:blank");
                return;
            }
            base.OnFormClosing(e);
        }

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

            switch (NavigationPolicy.Classify(uri, isNewWindow: true))
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
                case NavigationPolicy.Action.MarkClosing:
                default:
                    break;
            }
        }

        private void HandleNavigation(string uri, Action cancelAction, bool isNewWindow = false)
        {
            switch (NavigationPolicy.Classify(uri, isNewWindow))
            {
                case NavigationPolicy.Action.MarkClosing:
                    if (!_closeState.IsClosingConfirmed && !_closeState.IsHostCloseNavigationPending)
                        _closeState.BeginHostCloseNavigation();
                    break;
                case NavigationPolicy.Action.OpenExternal:
                    cancelAction();
                    OpenInDefaultBrowser(uri);
                    break;
                case NavigationPolicy.Action.Allow:
                default:
                    break;
            }
        }

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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _favicon?.Dispose();
                if (_disposeZipOnClose)
                    _zip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
