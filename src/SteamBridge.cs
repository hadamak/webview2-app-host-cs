using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// steam_bridge.dll と WebView2 を繋ぐ C# 側ブリッジ。
    /// DLL が存在しない場合は何もしない（Steam なし環境でも動作する）。
    ///
    /// JS↔C# 間のメッセージフォーマット:
    ///   { "source": "steam", "messageId": "...", "params": "...", "asyncId": 0 }
    ///   params は JSON 文字列として二重エンコードされる（DataContractJsonSerializer で扱うため）。
    ///   JS 側では JSON.stringify(params) で送信し、JSON.parse(msg.params) で受信する。
    /// </summary>
    internal sealed class SteamBridge : IDisposable
    {
        // ---------------------------------------------------------------------------
        // メッセージ DataContract
        // ---------------------------------------------------------------------------

        [DataContract]
        private sealed class SteamMessage
        {
            [DataMember(Name = "source")]
            public string Source { get; set; } = "";

            [DataMember(Name = "messageId")]
            public string MessageId { get; set; } = "";

            /// <summary>
            /// params は JSON 文字列として二重エンコードされた値。
            /// 例: JS 側が [42, "hello"] を送る場合、"[42,\"hello\"]" という文字列になる。
            /// </summary>
            [DataMember(Name = "params")]
            public string Params { get; set; } = "[]";

            [DataMember(Name = "asyncId")]
            public double AsyncId { get; set; } = -1.0;
        }

        private static readonly DataContractJsonSerializer s_serializer =
            new DataContractJsonSerializer(typeof(SteamMessage));

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
            AppLog.Log("INFO", "SteamBridge", $"初期化完了 (appId={appId}, dev={isDev})");

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
                AppLog.Log("INFO", "SteamBridge.TryCreate",
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
                var msg = Deserialize(webMessageJson);
                if (msg == null || msg.Source != "steam") return;

                // params は JS 側で JSON.stringify() された文字列なのでそのまま DLL へ渡す
                SteamBridge_SendMessage(msg.MessageId, msg.Params, msg.AsyncId);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridge.HandleWebMessage", ex.Message);
            }
        }

        // ---------------------------------------------------------------------------
        // DLL → JS への転送（DLL コールバック → UI スレッド → WebView2）
        // ---------------------------------------------------------------------------

        private void OnDllWebMessage(string messageId, string paramsJson, double asyncId)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            // DLL コールバックは任意スレッドから来るため BeginInvoke で UI スレッドに戻す
            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed) return;
                if (_webView.CoreWebView2 == null) return;

                try
                {
                    // params を JS 側で JSON.parse() できるよう文字列として埋め込む
                    var payload = Serialize(new SteamMessage
                    {
                        Source    = "steam",
                        MessageId = messageId,
                        Params    = string.IsNullOrEmpty(paramsJson) ? "{}" : paramsJson,
                        AsyncId   = asyncId
                    });

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
            AppLog.Log(lvl, "SteamBridge[DLL]", message ?? "");
        }

        // ---------------------------------------------------------------------------
        // シリアライズヘルパー（DataContractJsonSerializer で統一）
        // ---------------------------------------------------------------------------

        private static string Serialize(SteamMessage msg)
        {
            using var ms = new MemoryStream();
            s_serializer.WriteObject(ms, msg);
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static SteamMessage? Deserialize(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using var ms = new MemoryStream(bytes);
            return s_serializer.ReadObject(ms) as SteamMessage;
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
