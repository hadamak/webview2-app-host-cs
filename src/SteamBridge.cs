using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// steam_bridge.dll と WebView2 を繋ぐ C# 側ブリッジ。
    /// DLL が存在しない場合は何もしない（Steam なし環境でも動作する）。
    ///
    /// JS↔C# 間のメッセージフォーマット（JS→C# は JSON 文字列で送る）:
    ///   JS → C#: "{\"source\":\"steam\",\"messageId\":\"...\",\"params\":[...],\"asyncId\":1}"
    ///   C# → JS: { "source": "steam", "messageId": "...", "params": {...}, "asyncId": 1 }
    ///
    /// DataContractJsonSerializer は JS から届いた JSON 文字列の
    /// source / messageId / asyncId のエンベロープ解析にのみ使用する。
    /// params は JSON の生テキストとして ExtractParamsJson() で抽出し、
    /// 送信側は BuildOutgoingJson() で直接埋め込む。
    /// </summary>
    internal sealed class SteamBridge : IDisposable
    {
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogInfoDebug(string source, string message)
            => AppLog.Log("INFO", source, message);

        // ---------------------------------------------------------------------------
        // エンベロープ DataContract（params を含まない）
        // DataContractJsonSerializer は未知のフィールドを無視するため、
        // params が JSON 上に存在しても問題なく動作する。
        // ---------------------------------------------------------------------------

        [DataContract]
        private sealed class SteamEnvelope
        {
            [DataMember(Name = "source")]
            public string Source { get; set; } = "";

            [DataMember(Name = "messageId")]
            public string MessageId { get; set; } = "";

            [DataMember(Name = "asyncId")]
            public double AsyncId { get; set; } = -1.0;
        }

        private static readonly DataContractJsonSerializer s_envelopeSerializer =
            new DataContractJsonSerializer(typeof(SteamEnvelope));

        // ---------------------------------------------------------------------------
        // P/Invoke 定義
        // ---------------------------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private delegate void WebMessageCallbackDelegate(
            string messageId,
            string paramsJson,
            double asyncId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private delegate void LogCallbackDelegate(
            int level,
            string message);

        [DllImport("steam_bridge.dll", CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamBridge_Init(
            string appId,
            [MarshalAs(UnmanagedType.I1)] bool isDev,
            WebMessageCallbackDelegate msgCallback,
            LogCallbackDelegate logCallback);

        [DllImport("steam_bridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamBridge_Shutdown();

        [DllImport("steam_bridge.dll", CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Ansi)]
        private static extern void SteamBridge_SendMessage(
            string messageId,
            string paramsJson,
            double asyncId);

        [DllImport("steam_bridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamBridge_RunCallbacks();

        // ---------------------------------------------------------------------------
        // フィールド
        // ---------------------------------------------------------------------------

        private readonly WebView2  _webView;
        private readonly System.Windows.Forms.Timer _callbackTimer;
        private bool _disposed = false;

        // GC に回収されないようデリゲートをフィールドで保持（必須）
        private readonly WebMessageCallbackDelegate _msgDelegate;
        private readonly LogCallbackDelegate        _logDelegate;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        public SteamBridge(WebView2 webView, string appId, bool isDev)
        {
            _webView     = webView;
            _msgDelegate = OnDllWebMessage;
            _logDelegate = OnDllLog;

            SteamBridge_Init(appId, isDev, _msgDelegate, _logDelegate);
            LogInfoDebug("SteamBridge", $"初期化完了 (appId={appId}, dev={isDev})");

            // SteamAPI_RunCallbacks() を 100ms ごとに呼ぶ
            _callbackTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _callbackTimer.Tick += (s, e) =>
            {
                if (!_disposed) SteamBridge_RunCallbacks();
            };
            _callbackTimer.Start();
        }

        // ---------------------------------------------------------------------------
        // 静的ファクトリ: DLL が存在しない場合は null を返す
        // ---------------------------------------------------------------------------

        public static SteamBridge? TryCreate(WebView2 webView, string appId, bool isDev)
        {
            var dllPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "steam_bridge.dll");
            if (!File.Exists(dllPath))
            {
                LogInfoDebug("SteamBridge.TryCreate",
                    "steam_bridge.dll が見つかりません。Steam 機能は無効です。");
                return null;
            }

            try
            {
                return new SteamBridge(webView, appId, isDev);
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "SteamBridge.TryCreate",
                    "steam_bridge.dll のロードに失敗しました。Steam 機能は無効です。", ex);
                return null;
            }
        }

        // ---------------------------------------------------------------------------
        // JS → DLL への転送（WebMessageReceived から呼ばれる）
        // ---------------------------------------------------------------------------

        /// <summary>
        /// WebView2 の WebMessageReceived イベントから呼ぶ。
        /// source が "steam" 以外のメッセージは無視する。
        /// </summary>
        public void HandleWebMessage(string webMessageJson)
        {
            if (_disposed) return;
            try
            {
                var envelope = DeserializeEnvelope(webMessageJson);
                if (envelope == null || envelope.Source != "steam") return;

                // params は生の JSON 配列として抽出して DLL へ渡す
                var paramsJson = ExtractParamsJson(webMessageJson);
                SteamBridge_SendMessage(envelope.MessageId, paramsJson, envelope.AsyncId);

                // Steam 側の ScreenshotRequested_t が来ない環境向けに、
                // trigger-screenshot はホスト主導で直接キャプチャして返す。
                if (envelope.MessageId == "trigger-screenshot" &&
                    _webView.IsHandleCreated &&
                    !_webView.IsDisposed)
                {
                    _webView.BeginInvoke(new Action(async () =>
                    {
                        LogInfoDebug("SteamBridge.HandleWebMessage",
                            "trigger-screenshot を受信したため直接キャプチャを開始します");
                        await CaptureAndSendScreenshotAsync();
                    }));
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridge.HandleWebMessage", ex.Message);
            }
        }

        // ---------------------------------------------------------------------------
        // DLL → C# コールバック（任意スレッド → UI スレッド → WebView2）
        // ---------------------------------------------------------------------------

        private void OnDllWebMessage(string messageId, string paramsJson, double asyncId)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            // screenshot-requested はスクリーンショット撮影で完結する（JS 不要）
            if (messageId == "screenshot-requested")
            {
                _webView.BeginInvoke(new Action(async () =>
                {
                    await CaptureAndSendScreenshotAsync();
                }));
                return;
            }

            // その他のメッセージは JS へ転送する
            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                if (_webView.CoreWebView2 == null) return;

                try
                {
                    // params を生の JSON 値として埋め込む（二重エンコードなし）
                    var payload = BuildOutgoingJson(messageId, paramsJson, asyncId);
                    _webView.CoreWebView2.PostWebMessageAsString(payload);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "SteamBridge.OnDllWebMessage", ex.Message);
                }
            }));
        }

        private void OnDllLog(int level, string message)
        {
            var lvl = level switch { 1 => "WARN", 2 => "ERROR", _ => "INFO" };
            if (lvl == "INFO")
            {
                LogInfoDebug("SteamBridge[DLL]", message ?? "");
                return;
            }

            AppLog.Log(lvl, "SteamBridge[DLL]", message ?? "");
        }

        // ---------------------------------------------------------------------------
        // スクリーンショット（C# 側で完結。JS 不要）
        // ---------------------------------------------------------------------------

        private async System.Threading.Tasks.Task CaptureAndSendScreenshotAsync()
        {
            if (_disposed || _webView.CoreWebView2 == null) return;
            try
            {
                // WebView2 の内容を PNG として取得
                using var pngStream = new MemoryStream();
                await _webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, pngStream);
                pngStream.Position = 0;

                // PNG → RGB バイト列 → Base64
                using var bmp = new Bitmap(pngStream);
                var (base64, width, height) = BitmapToRgbBase64(bmp);

                // DLL の OnScreenshotData() に渡す
                // params: [base64RgbData, width, height]
                var paramsJson = $"[\"{base64}\",{width},{height}]";
                SteamBridge_SendMessage("screenshot-data", paramsJson, -1.0);
                LogInfoDebug("SteamBridge.Screenshot", $"送信完了 ({width}x{height})");
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridge.CaptureAndSendScreenshot", ex.Message, ex);
            }
        }

        /// <summary>
        /// Bitmap を Steam が要求する RGB バイト列（24bit、RGBA のアルファなし）に変換し Base64 で返す。
        /// LockBits で一括取得することで GetPixel ループより大幅に高速。
        /// </summary>
        private static (string base64, int width, int height) BitmapToRgbBase64(Bitmap bmp)
        {
            int width  = bmp.Width;
            int height = bmp.Height;

            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int stride   = bmpData.Stride;             // 行バイト数（負の場合あり）
                int rowBytes = Math.Abs(stride);
                var bgra     = new byte[rowBytes * height];
                Marshal.Copy(bmpData.Scan0, bgra, 0, bgra.Length);

                // BGRA（Windows DIB）→ RGB（Steam WriteScreenshot 要件）
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
                        // A (bgra[src + 3]) を破棄
                    }
                }
                return (Convert.ToBase64String(rgb), width, height);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        // ---------------------------------------------------------------------------
        // JSON ヘルパー
        // ---------------------------------------------------------------------------

        /// <summary>
        /// JSON 文字列から "params" キーの値を生の JSON テキストとして抽出する。
        /// WebView2 が生成した整形済み JSON のみを対象とするため、
        /// シンプルな括弧追跡で十分（ネスト・エスケープ文字を考慮済み）。
        /// 対応する値が見つからない場合は "[]" を返す。
        /// </summary>
        internal static string ExtractParamsJson(string json)
        {
            const string key = "\"params\":";
            int keyIdx = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIdx < 0) return "[]";

            int start = keyIdx + key.Length;
            // 空白をスキップ
            while (start < json.Length && json[start] == ' ') start++;
            if (start >= json.Length) return "[]";

            char opener = json[start];
            if (opener != '[' && opener != '{') return "[]";
            char closer = opener == '[' ? ']' : '}';

            int depth  = 0;
            bool inStr = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (c == '\\') i++;           // エスケープされた次の文字をスキップ
                    else if (c == '"') inStr = false;
                }
                else if (c == '"')   inStr = true;
                else if (c == opener) depth++;
                else if (c == closer && --depth == 0)
                    return json.Substring(start, i - start + 1);
            }
            return "[]";
        }

        /// <summary>
        /// DLL → JS の送信 JSON を構築する。
        /// params は生の JSON 値として直接埋め込む（DataContractJsonSerializer 不使用）。
        /// messageId は Steam が生成する ASCII ハイフン区切り文字列のみを想定するが、
        /// 安全のため最小エスケープを適用する。
        /// </summary>
        private static string BuildOutgoingJson(string messageId, string paramsJson, double asyncId)
        {
            var safeId    = EscapeJsonString(messageId ?? "");
            var safeParams = string.IsNullOrEmpty(paramsJson) ? "{}" : paramsJson;
            // asyncId は整数相当の double（JS の ++_asyncId）なので G 形式で十分
            var asyncStr  = asyncId.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
            return $"{{\"source\":\"steam\",\"messageId\":\"{safeId}\",\"params\":{safeParams},\"asyncId\":{asyncStr}}}";
        }

        /// <summary>
        /// JSON 文字列値に必要な最小限のエスケープ処理。
        /// </summary>
        private static string EscapeJsonString(string s) =>
            s.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("\n", "\\n")
             .Replace("\r", "\\r")
             .Replace("\t", "\\t");

        private static SteamEnvelope? DeserializeEnvelope(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using var ms = new MemoryStream(bytes);
            return s_envelopeSerializer.ReadObject(ms) as SteamEnvelope;
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
            try { SteamBridge_Shutdown(); }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "SteamBridge.Dispose", "SteamBridge_Shutdown 失敗", ex);
            }
        }
    }
}
