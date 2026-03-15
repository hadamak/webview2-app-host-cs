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

        // 閉じる確認フラグ
        private bool _closePending   = false;
        private bool _closeConfirmed = false;

        // visibilitychange 用
        private bool _isMinimized = false;

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
            Icon = Icon.ExtractAssociatedIcon(
                System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);

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
            wv.Settings.IsStatusBarEnabled            = false;
            wv.Settings.AreDefaultContextMenusEnabled = false;
            wv.Settings.IsZoomControlEnabled          = false;
            wv.Settings.AreDevToolsEnabled            = false;

            // カスタムスキームの登録（https://app.local/*）
            RegisterCustomScheme();

            // JS ブリッジの注入
            SetupHostObjectBridge();

            // <title> 変化をウィンドウタイトルに反映
            wv.DocumentTitleChanged += (s, _) =>
            {
                if (!string.IsNullOrEmpty(wv.DocumentTitle))
                    Text = wv.DocumentTitle;
            };

            // favicon 追従
            SetupFaviconTracking();

            // JS からのメッセージ受信
            wv.WebMessageReceived += OnWebMessageReceived;

            // index.html へナビゲート
            wv.Navigate("https://app.local/index.html");

            // 起動時フルスクリーン
            if (_config.Fullscreen)
                EnterFullscreen();
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

                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    var (start, end) = ParseRange(rangeHeader!, total);
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

        private static (long start, long end) ParseRange(string header, long total)
        {
            // "bytes=start-end" or "bytes=start-"
            var m = Regex.Match(header, @"bytes=(\d*)-(\d*)");
            if (!m.Success) return (0, total - 1);

            var startStr = m.Groups[1].Value;
            var endStr   = m.Groups[2].Value;

            long start = string.IsNullOrEmpty(startStr) ? 0            : long.Parse(startStr);
            long end   = string.IsNullOrEmpty(endStr)   ? total - 1    : long.Parse(endStr);

            // suffix range: "bytes=-500" → 末尾500バイト
            if (string.IsNullOrEmpty(startStr))
            {
                start = total - long.Parse(endStr);
                end   = total - 1;
            }

            return (Math.Max(0, start), Math.Min(end, total - 1));
        }

        // ---------------------------------------------------------------------------
        // JS ブリッジ
        // ---------------------------------------------------------------------------

        private void SetupHostObjectBridge()
        {
            const string script = @"
        window.GameBridge = {
            exitApp: function() {
                window.chrome.webview.postMessage(JSON.stringify({ cmd: 'exit' }));
            },
            toggleFullscreen: function() {
                window.chrome.webview.postMessage(JSON.stringify({ cmd: 'toggleFullscreen' }));
            },
            isFullscreen: function() {
                return new Promise((resolve) => {
                    const id = 'isfs_' + Date.now();
                    const handler = (e) => {
                        try {
                            const d = JSON.parse(e.data);
                            if (d.id === id) {
                                window.chrome.webview.removeEventListener('message', handler);
                                resolve(d.value);
                            }
                        } catch {}
                    };
                    window.chrome.webview.addEventListener('message', handler);
                    window.chrome.webview.postMessage(JSON.stringify({ cmd: 'isFullscreen', id }));
                });
            },
            confirmClose: function() {
                window.chrome.webview.postMessage(JSON.stringify({ cmd: 'confirmClose' }));
            },
            cancelClose: function() {
                window.chrome.webview.postMessage(JSON.stringify({ cmd: 'cancelClose' }));
            }
        };

        // appClosing リスナーの登録数を追跡する
        window._appClosingListenerCount = 0;
        const _origAddEL    = window.addEventListener.bind(window);
        const _origRemoveEL = window.removeEventListener.bind(window);
        window.addEventListener = function(type, fn, opts) {
            if (type === 'appClosing') window._appClosingListenerCount++;
            return _origAddEL(type, fn, opts);
        };
        window.removeEventListener = function(type, fn, opts) {
            if (type === 'appClosing')
                window._appClosingListenerCount = Math.max(0, window._appClosingListenerCount - 1);
            return _origRemoveEL(type, fn, opts);
        };

        // C# からのイベント受信
        window.chrome.webview.addEventListener('message', function(e) {
            let data;
            try { data = JSON.parse(e.data); } catch { return; }

            if (data.event === 'appClosing') {
                if (window._appClosingListenerCount > 0) {
                    window.dispatchEvent(new Event('appClosing'));
                } else {
                    GameBridge.confirmClose();
                }
                return;
            }

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

        console.log('[GameBridge] ready');
            ";

            _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string msg;
            try { msg = e.TryGetWebMessageAsString(); }
            catch { return; }

            // cmd を取り出す
            var cmdMatch = Regex.Match(msg, @"""cmd""\s*:\s*""([^""]+)""");
            if (!cmdMatch.Success) return;
            var cmd = cmdMatch.Groups[1].Value;

            switch (cmd)
            {
                case "exit":
                    // × ボタンと同じ appClosing フローを経由して閉じる
                    Close();
                    break;

                case "toggleFullscreen":
                    ToggleFullscreen();
                    break;

                case "isFullscreen":
                    var idMatch = Regex.Match(msg, @"""id""\s*:\s*""([^""]+)""");
                    if (!idMatch.Success) break;
                    {
                        var id  = idMatch.Groups[1].Value;
                        var val = _isFullscreen ? "true" : "false";
                        _webView.CoreWebView2.PostWebMessageAsString(
                            $"{{\"id\":\"{id}\",\"value\":{val}}}");
                    }
                    break;

                case "confirmClose":
                    _closeConfirmed = true;
                    Close();
                    break;

                case "cancelClose":
                    _closePending = false;
                    break;
            }
        }

        // ---------------------------------------------------------------------------
        // フルスクリーン
        // ---------------------------------------------------------------------------

        private void ToggleFullscreen()
        {
            if (_isFullscreen) ExitFullscreen(); else EnterFullscreen();
        }

        private void EnterFullscreen()
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
                        Icon = Icon.ExtractAssociatedIcon(
                            System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
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
                    WriteIco(resized, icoMs);
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
            // confirmClose 済み、または WebView2 未初期化なら素直に閉じる
            if (_closeConfirmed || _webView.CoreWebView2 == null)
            {
                base.OnFormClosing(e);
                return;
            }

            // 既に appClosing を送信して応答待ちの場合は閉じない
            if (_closePending)
            {
                e.Cancel = true;
                return;
            }

            // JS に appClosing を通知して閉じるのを一旦キャンセル
            e.Cancel      = true;
            _closePending = true;
            _webView.CoreWebView2.PostWebMessageAsString("{\"event\":\"appClosing\"}");
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

        // キー入力を WebView2 より先にフォームが受け取る
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F11)
            {
                ToggleFullscreen();
                return true;
            }
            if (keyData == Keys.Escape && _isFullscreen)
            {
                ExitFullscreen();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// 32x32 Bitmap を ICO 形式で Stream に書き出す。
        /// Icon コンストラクタは ICO 形式の Stream を要求するため。
        /// </summary>
        private static void WriteIco(Bitmap bmp, System.IO.Stream dest)
        {
            using var pngStream = new System.IO.MemoryStream();
            bmp.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            var pngBytes = pngStream.ToArray();

            // ICO ヘッダ構造: ICONDIR(6) + ICONDIRENTRY(16) + PNG data
            using var w = new System.IO.BinaryWriter(dest, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write((short)0);               // Reserved
            w.Write((short)1);               // Type: 1 = ICO
            w.Write((short)1);               // Count: 1 image
            w.Write((byte)bmp.Width);        // Width
            w.Write((byte)bmp.Height);       // Height
            w.Write((byte)0);                // ColorCount
            w.Write((byte)0);               // Reserved
            w.Write((short)1);              // Planes
            w.Write((short)32);             // BitCount
            w.Write((int)pngBytes.Length);  // SizeInBytes
            w.Write((int)22);               // Offset: 6 + 16 = 22
            w.Write(pngBytes);
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

        /// <param name="ownsInner">true = Dispose 時に inner を閉じる（デフォルト）。
        /// false = inner を共有する場合（ZIP ストリームを複数の SubStream で共有する場合）。</param>
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
            get => _position;
            set
            {
                _position = value;
                _inner.Position = _offset + value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0) return 0;
            count = (int)Math.Min(count, remaining);
            _inner.Position = _offset + _position;
            var read = _inner.Read(buffer, offset, count);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
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

        public override void Flush()  { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsInner) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------------------
    // NativeMethods
    // ---------------------------------------------------------------------------

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
