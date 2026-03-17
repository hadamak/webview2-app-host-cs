using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
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

        // visibilitychange 用
        private bool _isMinimized = false;

        // 終了確認用
        private bool _isClosingConfirmed = false;
        private bool _isClosingInProgress = false;

        // フルスクリーン用
        private bool             _isFullscreen    = false;
        private FormBorderStyle  _prevBorderStyle = FormBorderStyle.Sizable;
        private FormWindowState  _prevWindowState = FormWindowState.Normal;

        // favicon 管理（前のアイコンを Dispose するために保持）
        private Icon? _favicon = null;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        public App(ZipContentProvider zip, AppConfig config)
        {
            _zip    = zip;
            _config = config;

            // ウィンドウ基本設定
            Text            = config.Title;
            ClientSize      = new Size(config.Width, config.Height);
            StartPosition   = FormStartPosition.CenterScreen;

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
            wv.WindowCloseRequested += (s, _) =>
            {
                _isClosingConfirmed = true;
                Close();
            };

            // 外部リンク・ナビゲーションのハンドリング
            wv.NewWindowRequested += (s, e) => HandleNavigation(e.Uri, () => e.Handled = true);
            wv.NavigationStarting += (s, e) => HandleNavigation(e.Uri, () => e.Cancel  = true);

            // 遷移完了時の判定（beforeunload を経て about:blank に到達した ＝ 終了承諾）
            wv.NavigationCompleted += (s, e) =>
            {
                if (_isClosingInProgress && wv.Source == "about:blank")
                {
                    _isClosingConfirmed = true;
                    Close();
                }
            };

            // 標準のダイアログ（alert, confirm, prompt, beforeunload）を表示
            wv.ScriptDialogOpening += (s, e) => { /* デフォルト動作 */ };

            // C# から JS へのイベント通知の土台 & window.close() の標準化
            await wv.AddScriptToExecuteOnDocumentCreatedAsync(@"
                // window.close() を about:blank への遷移に置き換える。
                // これによりセキュリティ制限を回避し、かつ beforeunload を確実に発火させる。
                window.close = function() {
                    location.href = 'about:blank';
                };

                window.chrome.webview.addEventListener('message', function(e) {
                    let data;
                    try { data = JSON.parse(e.data); } catch { return; }

                    if (data.event === 'visibilityChange') {
                        Object.defineProperty(document, 'visibilityState', {
                            value: data.state, writable: true, configurable: true
                        });
                        Object.defineProperty(document, 'hidden', {
                            value: data.state === 'hidden', writable: true, configurable: true
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
            wv.Navigate("https://app.local/index.html");

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
                var total = stream.Length;

                if (!string.IsNullOrEmpty(rangeHeader) && stream.CanSeek)
                {
                    var range = ParseRange(rangeHeader!, total);
                    if (range == null)
                    {
                        stream.Dispose();
                        e.Response = _webView.CoreWebView2.Environment
                            .CreateWebResourceResponse(null, 416, "Range Not Satisfiable",
                                $"Content-Range: bytes */{total}\r\n" +
                                "Access-Control-Allow-Origin: *");
                        return;
                    }

                    var (start, end) = range.Value;
                    var length = end - start + 1;

                    stream.Seek(start, SeekOrigin.Begin);
                    // SubStream が stream の所有権を持ち Dispose 時に解放する
                    var rangeStream = new SubStream(stream, start, length);

                    var headers =
                        $"Content-Type: {mime}\r\n" +
                        $"Content-Range: bytes {start}-{end}/{total}\r\n" +
                        $"Content-Length: {length}\r\n" +
                        "Accept-Ranges: bytes\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Access-Control-Allow-Origin: *";

                    e.Response = _webView.CoreWebView2.Environment
                        .CreateWebResourceResponse(rangeStream, 206, "Partial Content", headers);
                }
                else
                {
                    var headers =
                        $"Content-Type: {mime}\r\n" +
                        $"Content-Length: {total}\r\n" +
                        "Accept-Ranges: bytes\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Access-Control-Allow-Origin: *";

                    e.Response = _webView.CoreWebView2.Environment
                        .CreateWebResourceResponse(stream, 200, "OK", headers);
                }
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Range ヘッダを解析して (start, end) を返す。
        /// フォーマット不正・逆転レンジは null（416 を返すべきケース）。
        /// end が total を超える場合は total-1 にクランプする（動画シーク互換）。
        /// </summary>
        private static (long start, long end)? ParseRange(string header, long total)
        {
            var m = Regex.Match(header, @"bytes=(\d*)-(\d*)");
            if (!m.Success) return null;

            var startStr = m.Groups[1].Value;
            var endStr   = m.Groups[2].Value;

            long start, end;

            if (string.IsNullOrEmpty(startStr))
            {
                // suffix range: "bytes=-500" → 末尾 500 バイト
                if (!long.TryParse(endStr, out var suffix) || suffix <= 0) return null;
                start = total - suffix;
                end   = total - 1;
            }
            else
            {
                start = long.Parse(startStr);
                end   = string.IsNullOrEmpty(endStr) ? total - 1 : long.Parse(endStr);
            }

            if (start > end) return null;

            // end のクランプは維持（ブラウザが total を超えた end を送ることがある）
            return (Math.Max(0, start), Math.Min(end, total - 1));
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
                    using var comStream = await wv.GetFaviconAsync(
                        CoreWebView2FaviconImageFormat.Png);

                    // COMStreamWrapper は Seek 不可なため MemoryStream に一度コピーする
                    using var pngMs = new System.IO.MemoryStream();
                    comStream.CopyTo(pngMs);
                    pngMs.Position = 0;

                    using var bmp     = new Bitmap(pngMs);
                    using var resized = new Bitmap(bmp, new Size(32, 32));

                    // MemoryStream 経由で ICO フォーマットに変換して Icon を生成
                    using var icoMs = new System.IO.MemoryStream();
                    IconUtils.WriteIco(resized, icoMs);
                    icoMs.Position = 0;
                    var newIcon = new Icon(icoMs);

                    Invoke(new Action(() =>
                    {
                        var oldIcon = _favicon;
                        _favicon = newIcon;
                        Icon     = newIcon;
                        oldIcon?.Dispose();
                    }));
                }
                catch (Exception ex)
                {
                    // favicon 取得・変換失敗は無視（アイコンは前の状態のまま）
                    _ = ex;
                }
            };
        }

        // ---------------------------------------------------------------------------
        // ウィンドウメッセージ処理
        // ---------------------------------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_isClosingConfirmed && _webView.CoreWebView2 != null)
            {
                e.Cancel = true;
                _isClosingInProgress = true;
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
            catch { }
        }

        private void HandleNavigation(string uri, Action cancelAction)
        {
            // about:blank への遷移は終了処理の合図として内部で許可する
            if (uri == "about:blank")
            {
                _isClosingInProgress = true;
                return;
            }

            // https://app.local/ 以外かつ http(s) の場合は既定のブラウザで開く
            if (!uri.StartsWith("https://app.local/") &&
                (uri.StartsWith("http://") || uri.StartsWith("https://")))
            {
                cancelAction();
                OpenInDefaultBrowser(uri);
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

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_isMinimized || _webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsString(
                "{\"event\":\"visibilityChange\",\"state\":\"visible\"}");
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (_isMinimized || _webView.CoreWebView2 == null) return;
            _webView.CoreWebView2.PostWebMessageAsString(
                "{\"event\":\"visibilityChange\",\"state\":\"hidden\"}");
        }
    }

    // ---------------------------------------------------------------------------
    // SubStream: Stream の部分範囲を別の Stream として公開（Range Request 用）
    // ---------------------------------------------------------------------------

    internal sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long   _offset;
        private readonly long   _length;
        private readonly bool   _ownsInner;
        private          long   _position;

        public SubStream(Stream inner, long offset, long length, bool ownsInner = true)
        {
            _inner     = inner;
            _offset    = offset;
            _length    = length;
            _ownsInner = ownsInner;
            _position  = 0;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => false;
        public override long Length   => _length;

        public override long Position
        {
            get { lock (_inner) return _position; }
            set
            {
                lock (_inner)
                {
                    _position = value;
                    _inner.Position = _offset + value;
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_inner)
            {
                var remaining = _length - _position;
                if (remaining <= 0) return 0;
                count = (int)Math.Min(count, remaining);
                _inner.Position = _offset + _position;
                var read = _inner.Read(buffer, offset, count);
                _position += read;
                return read;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_inner)
            {
                long newPos = origin switch
                {
                    SeekOrigin.Begin   => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End     => _length + offset,
                    _                  => throw new ArgumentException()
                };
                Position = newPos;
                return _position;
            }
        }

        public override void Flush()  { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsInner) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
