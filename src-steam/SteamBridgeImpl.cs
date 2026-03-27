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
using Steamworks.Data;

namespace WebView2AppHost
{
    /// <summary>
    /// Facepunch.Steamworks を用いた Steam ブリッジ実体クラス。
    ///
    /// JS から届く invoke メッセージをリフレクションで Steamworks.* の静的メソッド/プロパティへ
    /// ディスパッチする汎用パススルー型ブリッジ。API 追加時は JS・C# ともに変更不要。
    ///
    /// ディスパッチ優先順位:
    ///   1. TriggerScreenshot 特例（WebView2 キャプチャ → WriteScreenshot）
    ///   2. Achievement 構造体特例  Steam.Achievement.Trigger("ACH_NAME")
    ///      → new Achievement(name).Trigger() として実行
    ///   3. 静的メソッド  Steam.SteamUserStats.GetStatInt("NumGames")
    ///   4. 静的プロパティ getter  Steam.SteamClient.Name
    ///      （メソッドが見つからない場合に自動フォールバック）
    ///
    /// 【オーバーレイの動作について】
    /// SteamFriends.OpenOverlay(string) は ISteamFriends::ActivateGameOverlay と同等。
    /// WebView2 は DirectX フックに対応しないため画面への重畳は機能しないが、
    /// Steam の該当 UI ウィンドウ（実績・フレンドリスト等）を開く動作は正常に機能する。
    /// これは旧 C++ ブリッジの showOverlay 系 API と同じ動作。
    /// </summary>
    public sealed class SteamBridgeImpl : ISteamBridgeImpl
    {
        // デバッグログは #if DEBUG で囲む（プロジェクト内の他の箇所と統一）

        // ---------------------------------------------------------------------------
        // 定数
        // ---------------------------------------------------------------------------

        /// <summary>
        /// RestartAppIfNecessary が true を返した場合にスローする例外のメッセージ。
        /// SteamBridge.TryCreate がリフレクション越しにこのメッセージを識別して再スローし、
        /// App.TryInitSteam が Application.Exit() を呼ぶための識別子として使用する。
        /// </summary>
        internal const string SteamRestartRequiredMessage = "STEAM_RESTART_REQUIRED";

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
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, object> _handleRegistry
            = new System.Collections.Concurrent.ConcurrentDictionary<long, object>();
        private long _nextHandleId = 1;
        private bool _disposed;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        public SteamBridgeImpl(WebView2 webView, string appId, bool isDev)
        {
            _webView      = webView;
            _steamworkAsm = typeof(SteamClient).Assembly;

            if (!uint.TryParse(appId, out var id))
                throw new ArgumentException($"無効な AppID: {appId}");

            if (!isDev)
            {
                // リリースモード: SteamClient.Init より前に呼ぶ必要がある。
                // Steam 経由で起動されていない場合、Steam がアプリを再起動するため
                // true が返ったらプロセスを即座に終了しなければならない。
                // SteamBridgeImpl は別 DLL のためリフレクション越しに例外メッセージで
                // 通知し、呼び出し元（App.TryInitSteam）で Application.Exit() を行う。
                if (SteamClient.RestartAppIfNecessary(id))
                {
                    AppLog.Log("INFO", "SteamBridgeImpl",
                        "Steam 経由の起動ではありません。Steam による再起動を待機します。");
                    throw new InvalidOperationException(SteamRestartRequiredMessage);
                }
            }

            // asyncCallbacks=false: タイマーで RunCallbacks を手動呼び出しするため。
            // 補足: SteamClient.Init は内部で環境変数 SteamAppId / SteamGameId を設定する。
            //       isDev=true/false いずれも Init 経由で環境変数が設定されるため、
            //       steam_appid.txt なしで動作する（isDev=false は上記の
            //       RestartAppIfNecessary チェックを通過した後に Init を呼ぶ）。
            SteamClient.Init(id, asyncCallbacks: false);

#if DEBUG
            AppLog.Log("INFO", "SteamBridgeImpl", $"SteamClient 初期化完了 (appId={appId}, dev={isDev})");
#endif

            RegisterSteamCallbacks();

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

                var paramsJson = ExtractParamsJson(webMessageJson);
                var asyncId    = envelope.AsyncId;
                Task.Run(async () => { await DispatchInvokeAsync(paramsJson, asyncId); });
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridgeImpl.HandleWebMessage", ex.Message, ex);
            }
        }

        // ---------------------------------------------------------------------------
        // 汎用ディスパッチャ
        // ---------------------------------------------------------------------------

        private async Task DispatchInvokeAsync(string paramsJson, double asyncId)
        {
            string logClassName = "Unknown";
            string logMethodName = "Unknown";

            try
            {
                var paramsDict = _jss.Deserialize<Dictionary<string, object>>(paramsJson);

                var argsRaw = paramsDict.TryGetValue("args", out var ar) && ar is ArrayList al
                    ? al.Cast<object>().ToArray()
                    : Array.Empty<object>();

                // ---- 0. インスタンスメソッド呼び出し (Handle Dispatch) ----
                if (paramsDict.TryGetValue("handleId", out var rawHandleId))
                {
                    long handleId = Convert.ToInt64(rawHandleId);
                    if (!_handleRegistry.TryGetValue(handleId, out var targetInstance))
                        throw new InvalidOperationException($"ハンドルID {handleId} が見つかりません。");

                    logClassName = targetInstance.GetType().Name;
                    var instMethodName = paramsDict["methodName"]?.ToString() ?? throw new ArgumentException("methodName が空です。");
                    logMethodName = instMethodName;

                    var method = targetInstance.GetType().GetMethod(instMethodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    object? instResult;
                    if (method != null)
                    {
                        var convertedArgs = ConvertArgs(method.GetParameters(), argsRaw);
                        instResult = method.Invoke(targetInstance, convertedArgs);
                    }
                    else
                    {
                        // Fallback to property
                        var prop = targetInstance.GetType().GetProperty(instMethodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (prop != null)
                        {
                            instResult = prop.GetValue(targetInstance);
                        }
                        else
                        {
                            // Fallback to field
                            var field = targetInstance.GetType().GetField(instMethodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            if (field != null)
                            {
                                instResult = field.GetValue(targetInstance);
                            }
                            else
                            {
                                throw new MissingMethodException($"{logClassName}.{instMethodName} が見つかりません。");
                            }
                        }
                    }

                    if (instResult is Task instTask)
                    {
                        await instTask.ConfigureAwait(false);
                        instResult = instTask.GetType().IsGenericType ? instTask.GetType().GetProperty("Result")?.GetValue(instTask) : null;
                    }
                    SendResultToJs(asyncId, instResult, null);
                    return;
                }

                // ---- Static Method / Property / Constructor ----
                var rawClassName  = paramsDict.TryGetValue("className",  out var cn) ? cn as string : null;
                var rawMethodName = paramsDict.TryGetValue("methodName", out var mn) ? mn as string : null;

                if (string.IsNullOrEmpty(rawClassName) || string.IsNullOrEmpty(rawMethodName))
                    throw new ArgumentException("className または methodName が空です。");

                string className = rawClassName!;
                string methodName = rawMethodName!;

                logClassName = className;
                logMethodName = methodName;

                // ---- 1. スクリーンショット特例 ----
                if (className == "SteamScreenshots" && methodName == "TriggerScreenshot")
                {
                    await TriggerScreenshotAsync(asyncId);
                    return;
                }

                // ---- 2 & 3. 静的メソッド、プロパティ getter、またはコンストラクタ ----
                var type = _steamworkAsm.GetType($"Steamworks.{className}")
                           ?? _steamworkAsm.GetType($"Steamworks.Data.{className}")
                           ?? throw new TypeLoadException($"Steamworks または Steamworks.Data に {className} が見つかりません。");

                object? result;

                if (methodName == ".ctor" || methodName == "Create")
                {
                    var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                    var ctor = ctors.OrderBy(c => c.GetParameters().Length == argsRaw.Length ? 0 : 1).FirstOrDefault();
                    if (ctor == null) throw new MissingMethodException($"{className} のコンストラクタが見つかりません。");

                    result = ctor.Invoke(ConvertArgs(ctor.GetParameters(), argsRaw));
                }
                else
                {
                    result = InvokeStaticMemberOrProperty(type, methodName, argsRaw);
                }

                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    result = task.GetType().IsGenericType
                        ? task.GetType().GetProperty("Result")?.GetValue(task)
                        : null;
                }

                SendResultToJs(asyncId, result, null);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                AppLog.Log("ERROR", $"SteamBridgeImpl.Dispatch[{logClassName}.{logMethodName}]",
                    inner.Message, inner);
                SendResultToJs(asyncId, null, inner.Message);
            }
        }

        // ---------------------------------------------------------------------------
        // 静的メンバー呼び出し（メソッド優先 → プロパティ getter フォールバック）
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 静的メソッドを引数数・型でベストマッチを選んで呼び出す。
        /// メソッドが存在しない場合は静的プロパティ getter にフォールバックする。
        /// これにより SteamClient.Name / SteamClient.SteamId 等のプロパティを
        /// Steam.SteamClient.Name() として JS から呼び出せる。
        /// </summary>
        private static object? InvokeStaticMemberOrProperty(
            Type type, string memberName, object[] rawArgs)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == memberName)
                .OrderBy(m => m.GetParameters().Length == rawArgs.Length ? 0 : 1)
                .ToList();

            if (methods.Count > 0)
            {
                // 引数数が一致する候補を順に試す
                foreach (var m in methods.Where(m => m.GetParameters().Length == rawArgs.Length))
                {
                    try
                    {
                        return m.Invoke(null, ConvertArgs(m.GetParameters(), rawArgs));
                    }
                    catch (ArgumentException) { }
                    catch (InvalidCastException) { }
                }

                // 引数なしフォールバック
                var noParam = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
                if (noParam != null)
                    return noParam.Invoke(null, Array.Empty<object?>());

                // 最終フォールバック（最初の候補）
                var first = methods[0];
                return first.Invoke(null, ConvertArgs(first.GetParameters(), rawArgs));
            }

            // 静的プロパティ getter にフォールバック
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
            if (prop != null)
                return prop.GetValue(null);

            // 静的フィールド getter にフォールバック
            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
            if (field != null)
                return field.GetValue(null);

            throw new MissingMethodException($"{type.Name}.{memberName} が見つかりません。");
        }

        // ---------------------------------------------------------------------------
        // 引数変換
        // ---------------------------------------------------------------------------

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
            if (raw == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            // Nullable<T> のアンラップ
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null) targetType = underlying;

            // 型が既に一致
            if (targetType.IsAssignableFrom(raw.GetType())) return raw;

            // byte[] — JS の数値配列は JavaScriptSerializer により ArrayList<int> になる
            if (targetType == typeof(byte[]))
            {
                if (raw is byte[] already) return already;
                if (raw is ArrayList byteList)
                    return byteList.Cast<object>().Select(o => Convert.ToByte(o)).ToArray();
                if (raw is string b64)
                    return Convert.FromBase64String(b64);
            }

            // enum 変換
            if (targetType.IsEnum)
            {
                if (raw is string s) return Enum.Parse(targetType, s, ignoreCase: true);
                return Enum.ToObject(targetType, Convert.ToInt64(raw));
            }

            // 特殊な Facepunch 構造体のキャスト
            if (targetType == typeof(AppId)) return new AppId { Value = Convert.ToUInt32(raw) };
            if (targetType == typeof(SteamId)) return new SteamId { Value = Convert.ToUInt64(raw) };
            if (targetType == typeof(DepotId)) return new DepotId { Value = Convert.ToUInt32(raw) };
            if (targetType == typeof(GameId)) return new GameId { Value = Convert.ToUInt64(raw) };
            if (targetType == typeof(InventoryDefId)) return new InventoryDefId { Value = Convert.ToInt32(raw) };
            if (targetType == typeof(InventoryItemId)) return new InventoryItemId { Value = Convert.ToUInt64(raw) };

            // IConvertible（int, float, ulong, bool, string など）
            try
            {
                return Convert.ChangeType(raw, targetType,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                if (raw is string str)
                {
                    var parse = targetType.GetMethod("Parse",
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string) }, null);
                    if (parse != null) return parse.Invoke(null, new object[] { str });
                }
                throw;
            }
        }

        // ---------------------------------------------------------------------------
        // スクリーンショット特例
        // ---------------------------------------------------------------------------

        private async Task TriggerScreenshotAsync(double asyncId)
        {
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            var tcs = new TaskCompletionSource<bool>();
            _webView.BeginInvoke(new Action(async () =>
            {
                try   { await CaptureAndSendScreenshotAsync(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));

            await tcs.Task.ConfigureAwait(false);
            SendResultToJs(asyncId, null, null);
        }

        private async Task CaptureAndSendScreenshotAsync()
        {
            if (_disposed || _webView.CoreWebView2 == null) return;
            try
            {
                using var pngStream = new MemoryStream();
                await _webView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, pngStream);
                pngStream.Position = 0;

                using var bmp = new Bitmap(pngStream);
                var (rgb, width, height) = BitmapToRgb(bmp);

                // WriteScreenshot(byte[] pubRGB, int nWidth, int nHeight)
                SteamScreenshots.WriteScreenshot(rgb, width, height);

#if DEBUG
                AppLog.Log("INFO", "SteamBridgeImpl.Screenshot",
                    $"WriteScreenshot 完了 ({width}x{height})");
#endif
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridgeImpl.CaptureAndSendScreenshot", ex.Message, ex);
            }
        }

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
                    }
                }
                return (rgb, width, height);
            }
            finally { bmp.UnlockBits(bmpData); }
        }

        // ---------------------------------------------------------------------------
        // Steam コールバック → JS イベント転送
        // ---------------------------------------------------------------------------

        private void RegisterSteamCallbacks()
        {
            SteamScreenshots.OnScreenshotRequested += () =>
            {
                if (_webView.IsDisposed || !_webView.IsHandleCreated) return;
                _webView.BeginInvoke(new Action(async () =>
                {
                    await CaptureAndSendScreenshotAsync();
                }));
            };

            SteamUserStats.OnAchievementProgress += (name, current, max) =>
                PostEventToJs("OnAchievementProgress", new
                {
                    achievementName = name,
                    currentProgress = current,
                    maxProgress     = max,
                });

            SteamFriends.OnGameOverlayActivated += active =>
                PostEventToJs("OnGameOverlayActivated", new { active });

            SteamUser.OnMicroTxnAuthorizationResponse += (appId, orderId, authorized) =>
                PostEventToJs("OnMicroTxnAuthorizationResponse", new
                {
                    appId,
                    orderId    = orderId.ToString(),
                    authorized,
                });
        }

        // ---------------------------------------------------------------------------
        // JS への送信ヘルパー
        // ---------------------------------------------------------------------------

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
                        payload = $"{{\"source\":\"steam\",\"messageId\":\"invoke-result\"," +
                                  $"\"error\":\"{EscapeJsonString(error)}\"," +
                                  $"\"asyncId\":{FormatDouble(asyncId)}}}";
                    }
                    else
                    {
                        var wrappedResult = WrapSteamObjects(result);
                        payload = $"{{\"source\":\"steam\",\"messageId\":\"invoke-result\"," +
                                  $"\"result\":{SerializeResultValue(wrappedResult)}," +
                                  $"\"asyncId\":{FormatDouble(asyncId)}}}";
                    }
                    _webView.CoreWebView2.PostWebMessageAsString(payload);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "SteamBridgeImpl.SendResultToJs", ex.Message, ex);
                }
            }));
        }

        private object? WrapSteamObjects(object? result)
        {
            if (result == null) return null;
            var type = result.GetType();

            // IEnumerable な要素を再帰的にラップ（string/byte[] 等を除く）
            if (result is System.Collections.IEnumerable enumerable && !(result is string) && !(result is byte[]))
            {
                var list = new ArrayList();
                foreach (var item in enumerable)
                {
                    list.Add(WrapSteamObjects(item));
                }
                return list;
            }

            // Steamworks 名前空間のオブジェクト・構造体はハンドル化して JS 側で Proxy にさせる
            if (type.Namespace != null && type.Namespace.StartsWith("Steamworks") && !type.IsEnum)
            {
                long id = System.Threading.Interlocked.Increment(ref _nextHandleId);
                _handleRegistry[id] = result;
                return new { __isHandle = true, __handleId = id, className = type.Name };
            }

            return result;
        }

        private void PostEventToJs(string eventName, object eventParams)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null) return;
                try
                {
                    var payload = $"{{\"source\":\"steam\",\"event\":\"{EscapeJsonString(eventName)}\"," +
                                  $"\"params\":{_jss.Serialize(eventParams)}}}";
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

        private string SerializeResultValue(object? result)
        {
            if (result == null) return "null";
            if (result is bool b) return b ? "true" : "false";

            if (result is int    || result is uint  || result is long   ||
                result is ulong  || result is short  || result is ushort ||
                result is float  || result is double || result is decimal ||
                result is byte   || result is sbyte)
                return Convert.ToString(result,
                    System.Globalization.CultureInfo.InvariantCulture)!;

            if (result is string s) return $"\"{EscapeJsonString(s)}\"";

            // byte[] は数値配列として返す（JS 側で new Uint8Array(result) として使用可能）
            if (result is byte[] bytes)
                return "[" + string.Join(",", bytes.Select(bv => bv.ToString())) + "]";

            try   { return _jss.Serialize(result); }
            catch { return $"\"{EscapeJsonString(result.ToString() ?? "")}\""; }
        }

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

            int depth = 0; bool inStr = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; }
                else if (c == '"')        inStr = true;
                else if (c == opener)     depth++;
                else if (c == closer && --depth == 0)
                    return json.Substring(start, i - start + 1);
            }
            return "{}";
        }

        private static string EscapeJsonString(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"")
             .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

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
