using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Steamworks;

namespace WebView2AppHost
{
    /// <summary>
    /// Facepunch.Steamworks を用いた Steam ブリッジ実体クラス。
    ///
    /// JS から届く invoke メッセージをリフレクションで Steamworks.* の静的メソッドへ
    /// ディスパッチする汎用パススルー型ブリッジ。
    /// API 追加時は JS・C# ともに変更不要。
    ///
    /// スクリーンショット処理は C# 側で WebView2 をキャプチャし、RGB バイト配列を
    /// SteamScreenshots.AddScreenshot に直接渡す（Base64 変換なし）。
    /// </summary>
    internal sealed class SteamBridgeImpl : ISteamBridgeImpl
    {
        // ---------------------------------------------------------------------------
        // エンベロープ DataContract（JS → C# の外枠）
        // ---------------------------------------------------------------------------

        [DataContract]
        private sealed class SteamEnvelope
        {
            [DataMember(Name = "source")]    public string Source    { get; set; } = "";
            [DataMember(Name = "messageId")] public string MessageId { get; set; } = "";
            [DataMember(Name = "asyncId")]   public double AsyncId   { get; set; } = -1.0;
        }

        private static readonly DataContractJsonSerializer s_envelopeSerializer =
            new DataContractJsonSerializer(typeof(SteamEnvelope));

        // ---------------------------------------------------------------------------
        // フィールド
        // ---------------------------------------------------------------------------

        private readonly WebView2                    _webView;
        private readonly System.Windows.Forms.Timer _callbackTimer;
        private readonly JavaScriptSerializer        _jss = new JavaScriptSerializer();
        private readonly Assembly                    _steamworkAsm;
        private bool _disposed;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        public SteamBridgeImpl(WebView2 webView, string appId, bool isDev)
        {
            _webView      = webView;
            _steamworkAsm = typeof(SteamClient).Assembly;

            // Facepunch.Steamworks の初期化
            if (uint.TryParse(appId, out var id))
                SteamClient.Init(id, false);
            else
                throw new ArgumentException($"無効な AppID: {appId}");

#if DEBUG
            AppLog.Log("INFO", "SteamBridgeImpl", $"SteamClient 初期化完了 (appId={appId}, dev={isDev})");
#endif

            // Steam コールバックイベントを JS へ転送する
            RegisterSteamCallbacks();

            // SteamClient.RunCallbacks() を 100ms ごとに呼ぶ
            _callbackTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _callbackTimer.Tick += (s, e) =>
            {
                if (!_disposed) SteamClient.RunCallbacks();
            };
            _callbackTimer.Start();
        }

        // ---------------------------------------------------------------------------
        // JS → C# メッセージ受信
        // ---------------------------------------------------------------------------

        public void HandleWebMessage(string webMessageJson)
        {
            if (_disposed) return;
            try
            {
                var envelope = DeserializeEnvelope(webMessageJson);
                if (envelope == null || envelope.Source != "steam") return;

                if (envelope.MessageId != "invoke")
                {
                    AppLog.Log("WARN", "SteamBridgeImpl.HandleWebMessage",
                        $"未知の messageId: {envelope.MessageId}");
                    return;
                }

                // params オブジェクト（JSON文字列）を抽出
                var paramsJson = ExtractParamsJson(webMessageJson);

                // 非同期ディスパッチ（UI スレッドをブロックしない）
                var asyncId = envelope.AsyncId;
                Task.Run(async () =>
                {
                    await DispatchInvokeAsync(paramsJson, asyncId);
                });
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridgeImpl.HandleWebMessage", ex.Message, ex);
            }
        }

        // ---------------------------------------------------------------------------
        // 汎用ディスパッチャ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// paramsJson = {"className":"SteamUserStats","methodName":"SetAchievement","args":["ACH_WIN"]}
        /// </summary>
        private async Task DispatchInvokeAsync(string paramsJson, double asyncId)
        {
            string? className  = null;
            string? methodName = null;
            try
            {
                // JavaScriptSerializer でパース（net472 GAC ライブラリ、追加 NuGet 不要）
                var paramsDict = _jss.Deserialize<Dictionary<string, object>>(paramsJson);

                className  = paramsDict.TryGetValue("className",  out var cn) ? cn as string : null;
                methodName = paramsDict.TryGetValue("methodName", out var mn) ? mn as string : null;
                var argsRaw = paramsDict.TryGetValue("args", out var ar) && ar is ArrayList al
                    ? al.Cast<object>().ToArray()
                    : Array.Empty<object>();

                if (className == null || className == "" || methodName == null || methodName == "")
                    throw new ArgumentException("className または methodName が空です。");

                // ---------- スクリーンショット特例 ----------
                if (className == "SteamScreenshots" && methodName == "TriggerScreenshot")
                {
                    await TriggerScreenshotAsync(asyncId);
                    return;
                }

                // ---------- 汎用リフレクションディスパッチ ----------
                var type = _steamworkAsm.GetType($"Steamworks.{className}")
                    ?? throw new TypeLoadException($"Steamworks.{className} が見つかりません。");

                var (method, convertedArgs) = ResolveMethod(type, methodName, argsRaw);

                object? result = method.Invoke(null, convertedArgs);

                // Task / Task<T> の await
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    // Task<T> の場合は Result を取得
                    var taskType = task.GetType();
                    if (taskType.IsGenericType)
                    {
                        var resultProp = taskType.GetProperty("Result");
                        result = resultProp?.GetValue(task);
                    }
                    else
                    {
                        result = null;
                    }
                }

                SendResultToJs(asyncId, result, null);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                AppLog.Log("ERROR", $"SteamBridgeImpl.Dispatch[{className}.{methodName}]",
                    inner.Message, inner);
                SendResultToJs(asyncId, null, inner.Message);
            }
        }

        // ---------------------------------------------------------------------------
        // メソッド解決と引数変換
        // ---------------------------------------------------------------------------

        /// <summary>
        /// オーバーロードを考慮してベストマッチのメソッドを返す。
        /// 引数の数が一致するもので型変換できる候補を優先する。
        /// </summary>
        private static (MethodInfo method, object?[] args) ResolveMethod(
            Type type, string methodName, object[] rawArgs)
        {
            var candidates = type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == methodName)
                .OrderBy(m => m.IsStatic ? 0 : 1)   // static を優先
                .ThenBy(m => m.GetParameters().Length == rawArgs.Length ? 0 : 1) // 引数数一致優先
                .ToList();

            if (candidates.Count == 0)
                throw new MissingMethodException($"{type.Name}.{methodName} が見つかりません。");

            // 引数数が一致する候補を試す
            foreach (var m in candidates.Where(m => m.GetParameters().Length == rawArgs.Length))
            {
                try
                {
                    var converted = ConvertArgs(m.GetParameters(), rawArgs);
                    return (m, converted);
                }
                catch { /* 次の候補へ */ }
            }

            // 引数なしのものも試す（rawArgs が空の場合など）
            var noParam = candidates.FirstOrDefault(m => m.GetParameters().Length == 0);
            if (noParam != null)
                return (noParam, Array.Empty<object?>());

            // 最終フォールバック：最初の候補で試みる
            var fallback = candidates[0];
            return (fallback, ConvertArgs(fallback.GetParameters(), rawArgs));
        }

        /// <summary>
        /// JSON からデシリアライズされた rawArgs（string/int/bool/double など）を
        /// ターゲットメソッドの ParameterInfo に合わせて型変換する。
        /// </summary>
        private static object?[] ConvertArgs(ParameterInfo[] parameters, object[] rawArgs)
        {
            var result = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var raw = i < rawArgs.Length ? rawArgs[i] : null;
                result[i] = ConvertArg(raw, parameters[i].ParameterType);
            }
            return result;
        }

        private static object? ConvertArg(object? raw, Type targetType)
        {
            if (raw == null) return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            // Nullable<T> のアンラップ
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null) targetType = underlying;

            // 型が既に一致
            if (targetType.IsAssignableFrom(raw.GetType())) return raw;

            // enum 変換
            if (targetType.IsEnum)
            {
                if (raw is string s) return Enum.Parse(targetType, s, ignoreCase: true);
                return Enum.ToObject(targetType, Convert.ToInt64(raw));
            }

            // IConvertible で対応できる型（int, float, ulong, bool, string など）
            try
            {
                return Convert.ChangeType(raw, targetType,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                // 文字列 → 対象型のパース を試みる
                if (raw is string str)
                {
                    var parseMethod = targetType.GetMethod("Parse",
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string) }, null);
                    if (parseMethod != null) return parseMethod.Invoke(null, new object[] { str });
                }
                throw;
            }
        }

        // ---------------------------------------------------------------------------
        // スクリーンショット特例処理
        // ---------------------------------------------------------------------------

        private async Task TriggerScreenshotAsync(double asyncId)
        {
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            // UI スレッドでキャプチャを実行
            var tcs = new TaskCompletionSource<bool>();
            _webView.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await CaptureAndAddScreenshotAsync();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }));

            await tcs.Task.ConfigureAwait(false);
            SendResultToJs(asyncId, null, null);
        }

        private async Task CaptureAndAddScreenshotAsync()
        {
            if (_disposed || _webView.CoreWebView2 == null) return;
            try
            {
                using var pngStream = new MemoryStream();
                await _webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, pngStream);
                pngStream.Position = 0;

                // PNG → RGB バイト配列（Base64 変換なし）
                using var bmp = new Bitmap(pngStream);
                var (rgb, width, height) = BitmapToRgb(bmp);

                // Facepunch.Steamworks 2.3.3 の WriteScreenshot に RGB バイト配列を直接渡す
                // シグネチャ: SteamScreenshots.WriteScreenshot(byte[] pubRGB, int nWidth, int nHeight)
                SteamScreenshots.WriteScreenshot(rgb, width, height);

#if DEBUG
                AppLog.Log("INFO", "SteamBridgeImpl.Screenshot", $"WriteScreenshot 完了 ({width}x{height})");
#endif

            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridgeImpl.CaptureAndAddScreenshot", ex.Message, ex);
            }
        }

        /// <summary>
        /// Bitmap を Steam が要求する RGB バイト配列（24bit）に変換する。
        /// LockBits による一括変換で高速処理する。
        /// </summary>
        private static (byte[] rgb, int width, int height) BitmapToRgb(Bitmap bmp)
        {
            int width  = bmp.Width;
            int height = bmp.Height;

            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int stride   = bmpData.Stride;
                int rowBytes = Math.Abs(stride);
                var bgra     = new byte[rowBytes * height];
                Marshal.Copy(bmpData.Scan0, bgra, 0, bgra.Length);

                // BGRA（Windows DIB）→ RGB（Steam 要件）
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
                        // A は破棄
                    }
                }
                return (rgb, width, height);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }

        // ---------------------------------------------------------------------------
        // Steam コールバック → JS イベント転送
        // ---------------------------------------------------------------------------

        private void RegisterSteamCallbacks()
        {
            // スクリーンショット要求（Steam オーバーレイのカメラボタン）
            SteamScreenshots.OnScreenshotRequested += () =>
            {
                if (_webView.IsDisposed || !_webView.IsHandleCreated) return;
                _webView.BeginInvoke(new Action(async () =>
                {
                    await CaptureAndAddScreenshotAsync();
                }));
            };

            // 実績の進捗通知
            SteamUserStats.OnAchievementProgress += (name, current, max) =>
            {
                PostEventToJs("OnAchievementProgress", new
                {
                    achievementName = name,
                    currentProgress = current,
                    maxProgress     = max,
                });
            };

            // Steam オーバーレイ開閉
            SteamFriends.OnGameOverlayActivated += active =>
            {
                PostEventToJs("OnGameOverlayActivated", new { active });
            };

            // マイクロトランザクション認証（必要な場合）
            SteamUser.OnMicroTxnAuthorizationResponse += (appId, orderId, authorized) =>
            {
                PostEventToJs("OnMicroTxnAuthorizationResponse", new
                {
                    appId,
                    orderId    = orderId.ToString(),
                    authorized,
                });
            };
        }

        // ---------------------------------------------------------------------------
        // JS への送信ヘルパー
        // ---------------------------------------------------------------------------

        /// <summary>
        /// invoke-result を JS へ送る。error が null でない場合はエラー応答。
        /// </summary>
        private void SendResultToJs(double asyncId, object? result, string? error)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null) return;
                try
                {
                    string payload;
                    if (error != null)
                    {
                        var safeErr = EscapeJsonString(error);
                        var asyncStr = FormatDouble(asyncId);
                        payload = $"{{\"source\":\"steam\",\"messageId\":\"invoke-result\",\"error\":\"{safeErr}\",\"asyncId\":{asyncStr}}}";
                    }
                    else
                    {
                        var resultJson = SerializeResultValue(result);
                        var asyncStr   = FormatDouble(asyncId);
                        payload = $"{{\"source\":\"steam\",\"messageId\":\"invoke-result\",\"result\":{resultJson},\"asyncId\":{asyncStr}}}";
                    }
                    _webView.CoreWebView2.PostWebMessageAsString(payload);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "SteamBridgeImpl.SendResultToJs", ex.Message, ex);
                }
            }));
        }

        /// <summary>
        /// Steam イベントを JS に送る。params は匿名オブジェクト（JavaScriptSerializer でシリアライズ）。
        /// </summary>
        private void PostEventToJs(string eventName, object eventParams)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null) return;
                try
                {
                    var safeEvent  = EscapeJsonString(eventName);
                    var paramsJson = _jss.Serialize(eventParams);
                    var payload    = $"{{\"source\":\"steam\",\"event\":\"{safeEvent}\",\"params\":{paramsJson}}}";
                    _webView.CoreWebView2.PostWebMessageAsString(payload);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "SteamBridgeImpl.PostEventToJs", ex.Message, ex);
                }
            }));
        }

        // ---------------------------------------------------------------------------
        // JSON ヘルパー
        // ---------------------------------------------------------------------------

        /// <summary>
        /// result 値を JSON 値文字列に変換する。
        /// JavaScriptSerializer でシリアライズし、プリミティブ・オブジェクト・null を処理する。
        /// </summary>
        private string SerializeResultValue(object? result)
        {
            if (result == null) return "null";

            // bool
            if (result is bool b) return b ? "true" : "false";

            // 数値系
            if (result is int    || result is uint  || result is long   ||
                result is ulong  || result is short  || result is ushort ||
                result is float  || result is double || result is decimal ||
                result is byte   || result is sbyte)
            {
                return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture)!;
            }

            // string
            if (result is string s) return $"\"{EscapeJsonString(s)}\"";

            // その他（オブジェクト、配列）は JavaScriptSerializer でシリアライズ
            try { return _jss.Serialize(result); }
            catch { return $"\"{EscapeJsonString(result.ToString() ?? "")}\""; }
        }

        /// <summary>
        /// JSON の "params" フィールド値（オブジェクトか配列）を生テキストとして抽出する。
        /// </summary>
        internal static string ExtractParamsJson(string json)
        {
            const string key = "\"params\":";
            int keyIdx = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIdx < 0) return "{}";

            int start = keyIdx + key.Length;
            while (start < json.Length && json[start] == ' ') start++;
            if (start >= json.Length) return "{}";

            char opener = json[start];
            if (opener != '[' && opener != '{') return "{}";
            char closer = opener == '[' ? ']' : '}';

            int  depth  = 0;
            bool inStr  = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inStr = false;
                }
                else if (c == '"')        inStr = true;
                else if (c == opener)     depth++;
                else if (c == closer && --depth == 0)
                    return json.Substring(start, i - start + 1);
            }
            return "{}";
        }

        private static string EscapeJsonString(string s) =>
            s.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("\n", "\\n")
             .Replace("\r", "\\r")
             .Replace("\t", "\\t");

        private static string FormatDouble(double d) =>
            d.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

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
            try { SteamClient.Shutdown(); }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "SteamBridgeImpl.Dispose", "SteamClient.Shutdown 失敗", ex);
            }
        }
    }
}
