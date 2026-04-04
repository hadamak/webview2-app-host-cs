using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    ///              - CapturePreview([path]) → { path: string, width: number, height: number }
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
        private readonly JavaScriptSerializer _jss = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
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

                var args = p != null && p.TryGetValue("args", out var argsVal) && argsVal is ArrayList al
                    ? al
                    : new ArrayList();

                DispatchClassName(className!, methodName!, args, asyncId);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "InternalHostPlugin.HandleWebMessage", ex.Message, ex);
            }
        }

        // ---------------------------------------------------------------------------
        // 第1段ディスパッチ: className → カテゴリ
        // ---------------------------------------------------------------------------

        private void DispatchClassName(string className, string methodName, ArrayList args, double asyncId)
        {
            switch (className)
            {
                case "WebView":
                    DispatchWebView(methodName, args, asyncId);
                    break;

                // 新カテゴリはここに case を追加する
                // case "Window":
                //     DispatchWindow(methodName, args, asyncId);
                //     break;

                default:
                    SendError(asyncId, $"未知のクラス名: {className}");
                    break;
            }
        }

        // ---------------------------------------------------------------------------
        // 第2段ディスパッチ: WebView カテゴリ
        // ---------------------------------------------------------------------------

        private void DispatchWebView(string methodName, ArrayList args, double asyncId)
        {
            switch (methodName)
            {
                case "CapturePreview":
                    var outputPath = args.Count > 0 ? args[0] as string : null;
                    Task.Run(async () => await CapturePreviewAsync(outputPath, asyncId));
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
        /// WebView2 の現在の表示内容を PNG ファイルとして書き出し、
        /// ファイルパスと画像サイズを JS に返す。
        ///
        /// 引数:
        ///   path (string, 省略可) — 書き出し先ファイルパス。
        ///     相対パスは EXE 隣接ディレクトリ基準。
        ///     省略時は %TEMP%\webview2_capture_{guid}.png を自動生成する。
        ///
        /// JS 側の戻り値: { path: string, width: number, height: number }
        ///
        /// CoreWebView2 API は UI スレッドで呼ぶ必要があるため BeginInvoke で移譲し、
        /// TaskCompletionSource で非同期完了を待つ。
        /// </summary>
        private async Task CapturePreviewAsync(string? outputPath, double asyncId)
        {
            // パスの解決
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.Combine(
                    Path.GetTempPath(),
                    $"webview2_capture_{Guid.NewGuid():N}.png");
            }
            else if (!Path.IsPathRooted(outputPath))
            {
                outputPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    outputPath);
            }

            // 書き出し先ディレクトリが存在しない場合は作成する
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tcs = new TaskCompletionSource<(string? path, int width, int height)>();

            _webView.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (_disposed || _webView.CoreWebView2 == null)
                    {
                        tcs.SetException(new InvalidOperationException("WebView2 が利用できません。"));
                        return;
                    }

                    using var fileStream = File.Open(outputPath, FileMode.Create, FileAccess.ReadWrite);
                    await _webView.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, fileStream);

                    // PNG ヘッダから幅・高さを読む（IHDR チャンク: オフセット 16〜23 バイト）
                    fileStream.Position = 16;
                    var buf = new byte[8];
                    fileStream.Read(buf, 0, 8);
                    int width  = (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
                    int height = (buf[4] << 24) | (buf[5] << 16) | (buf[6] << 8) | buf[7];

                    tcs.SetResult((outputPath, width, height));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }));

            try
            {
                var (path, width, height) = await tcs.Task.ConfigureAwait(false);

#if DEBUG
                AppLog.Log("INFO", "InternalHostPlugin.WebView.CapturePreview",
                    $"キャプチャ完了 ({width}x{height}) → {path}");
#endif
                SendResult(asyncId,
                    $"{{\"path\":\"{EscapeJsonString(path ?? "")}\"," +
                    $"\"width\":{width}," +
                    $"\"height\":{height}}}");
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