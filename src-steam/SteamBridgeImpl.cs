using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections;
using System.Web.Script.Serialization;
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
    /// JSON シリアライザの選択:
    ///   受信メッセージの解析・結果の送信ともに System.Web.Script.Serialization.JavaScriptSerializer を使用する。
    ///   プラグインが受け取る args の型は JS 由来で不定（数値/文字列/配列/オブジェクト混在）のため、
    ///   Dictionary<string, object> や ArrayList による柔軟な型解釈が必要。
    ///   ホスト本体の固定スキーマ（AppConfig 等）は DataContractJsonSerializer を使い続ける。
    ///
    /// ディスパッチ優先順位:
    ///   1. TriggerScreenshot 特例（WebView2 キャプチャ → WriteScreenshot）
    ///   2. Achievement 構造体特例  Steam.Achievement.Trigger("ACH_NAME")
    ///      → new Achievement(name).Trigger() として実行
    ///   3. 静的メソッド  Steam.SteamUserStats.GetStatInt("NumGames")
    ///   4. 静的プロパティ getter  Steam.SteamClient.Name
    ///      （メソッドが見つからない場合に自動フォールバック）
    /// </summary>
    public sealed class SteamBridgeImpl : ISteamBridgeImpl
    {
        // ---------------------------------------------------------------------------
        // 定数
        // ---------------------------------------------------------------------------

        internal const string SteamRestartRequiredMessage = "STEAM_RESTART_REQUIRED";

        private static readonly JavaScriptSerializer s_serializer = new JavaScriptSerializer();

        // ---------------------------------------------------------------------------
        // フィールド
        // ---------------------------------------------------------------------------

        private readonly WebView2                    _webView;
        private readonly System.Windows.Forms.Timer _callbackTimer;
        private readonly Assembly                    _steamworkAsm;
        private readonly HashSet<string>             _allowedClassNames;
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

            _allowedClassNames = new HashSet<string>(
                _steamworkAsm.GetExportedTypes()
                    .Where(t => t.Namespace == "Steamworks" || t.Namespace == "Steamworks.Data")
                    .Select(t => t.Name),
                StringComparer.Ordinal);

            if (!uint.TryParse(appId, out var id))
                throw new ArgumentException($"無効な AppID: {appId}");

            if (!isDev)
            {
                if (SteamClient.RestartAppIfNecessary(id))
                {
                    AppLog.Log("INFO", "SteamBridgeImpl",
                        "Steam 経由の起動ではありません。Steam による再起動を待機します。");
                    throw new InvalidOperationException(SteamRestartRequiredMessage);
                }
            }

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
            if (_disposed || string.IsNullOrWhiteSpace(webMessageJson)) return;

            try
            {
                // JavaScriptSerializer でデシリアライズする。
                // プラグインが受け取る args は型不定（JS の number/string/array/object 混在）のため
                // Dictionary<string, object> などによる動的型解釈が必要。
                // ホスト固定スキーマ（AppConfig 等）は DataContractJsonSerializer を使い続ける。
                var msg = s_serializer.Deserialize<Dictionary<string, object>>(webMessageJson);
                if (msg == null) return;

                if (!msg.TryGetValue("source", out var srcObj) || 
                    !string.Equals(srcObj?.ToString(), "steam", StringComparison.OrdinalIgnoreCase)) return;

                msg.TryGetValue("messageId", out var messageIdObj);
                var messageId = messageIdObj?.ToString();

                msg.TryGetValue("asyncId", out var asyncIdObj);
                var asyncId = (asyncIdObj is IConvertible cv) ? cv.ToDouble(null) : -1.0;

                if (messageId == "release")
                {
                    if (msg.TryGetValue("params", out var pObj) && pObj is IDictionary pDict)
                    {
                        if (pDict.Contains("handleId") && pDict["handleId"] is IConvertible hId)
                        {
                            long handleId = hId.ToInt64(null);
                            _handleRegistry.TryRemove(handleId, out _);
#if DEBUG
                            AppLog.Log("INFO", "SteamBridgeImpl.Release", $"ハンドル {handleId} を解放しました");
#endif
                        }
                    }
                    return;
                }

                if (messageId != "invoke")
                {
                    AppLog.Log("WARN", "SteamBridgeImpl.HandleWebMessage", $"未知の messageId: {messageId}");
                    return;
                }

                if (!msg.TryGetValue("params", out var paramsVal) || !(paramsVal is Dictionary<string, object> paramsObj))
                    return;

                Task.Run(async () => { await DispatchInvokeAsync(paramsObj, asyncId); });
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "SteamBridgeImpl.HandleWebMessage", ex.Message, ex);
            }
        }

        // ---------------------------------------------------------------------------
        // 汎用ディスパッチャ
        // ---------------------------------------------------------------------------

        private async Task DispatchInvokeAsync(Dictionary<string, object> paramsObj, double asyncId)
        {
            string logClassName  = "Unknown";
            string logMethodName = "Unknown";

            try
            {
                // args は ArrayList → object?[] に変換して以降の処理へ渡す
                var argsRaw = paramsObj.TryGetValue("args", out var argsVal) && argsVal is ArrayList arr
                    ? arr.Cast<object?>().ToArray()
                    : Array.Empty<object?>();

                // ---- 0. インスタンスメソッド呼び出し (Handle Dispatch) ----
                if (paramsObj.TryGetValue("handleId", out var handleToken) && handleToken != null)
                {
                    long handleId = (handleToken is IConvertible cv) ? cv.ToInt64(null) : 0;
                    if (!_handleRegistry.TryGetValue(handleId, out var targetInstance))
                        throw new InvalidOperationException($"ハンドルID {handleId} が見つかりません。");

                    logClassName  = targetInstance.GetType().Name;
                    var instMethodName = paramsObj.TryGetValue("methodName", out var mName) ? mName?.ToString() : null;
                    if (string.IsNullOrEmpty(instMethodName))
                        throw new ArgumentException("methodName が空です。");
                    logMethodName = instMethodName!;

                    var method = targetInstance.GetType().GetMethod(instMethodName!,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                    object? instResult;
                    if (method != null)
                    {
                        var convertedArgs = ConvertArgs(method.GetParameters(), argsRaw);
                        instResult = method.Invoke(targetInstance, convertedArgs);
                    }
                    else
                    {
                        var prop = targetInstance.GetType().GetProperty(instMethodName!,
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                        if (prop != null)
                            instResult = prop.GetValue(targetInstance);
                        else
                        {
                            var field = targetInstance.GetType().GetField(instMethodName!,
                                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            if (field != null)
                                instResult = field.GetValue(targetInstance);
                            else
                                throw new MissingMethodException($"{logClassName}.{instMethodName} が見つかりません。");
                        }
                    }

                    if (instResult is Task instTask)
                    {
                        await instTask.ConfigureAwait(false);
                        instResult = instTask.GetType().IsGenericType
                            ? instTask.GetType().GetProperty("Result")?.GetValue(instTask) : null;
                    }
                    SendResultToJs(asyncId, instResult, null);
                    return;
                }

                // ---- 静的メソッド / プロパティ / コンストラクタ ----
                paramsObj.TryGetValue("className", out var classNameObj);
                paramsObj.TryGetValue("methodName", out var methodNameObj);
                var className  = classNameObj?.ToString();
                var methodName = methodNameObj?.ToString();

                if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(methodName))
                    throw new ArgumentException("className または methodName が空です。");

                logClassName  = className!;
                logMethodName = methodName!;

                // ---- 1. スクリーンショット特例 ----
                if (className == "SteamScreenshots" && methodName == "TriggerScreenshot")
                {
                    await TriggerScreenshotAsync(asyncId);
                    return;
                }

                // ---- 2 & 3. 静的メンバー / コンストラクタ ----
                if (!_allowedClassNames.Contains(className!))
                    throw new TypeLoadException($"クラス '{className}' はホワイトリストに含まれていません。");

                var type = _steamworkAsm.GetType($"Steamworks.{className}")
                           ?? _steamworkAsm.GetType($"Steamworks.Data.{className}")
                           ?? throw new TypeLoadException($"Steamworks または Steamworks.Data に {className} が見つかりません。");

                object? result;

                if (methodName == ".ctor" || methodName == "Create")
                {
                    var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                    var ctor  = ctors.OrderBy(c => c.GetParameters().Length == argsRaw.Length ? 0 : 1)
                                     .FirstOrDefault()
                        ?? throw new MissingMethodException($"{className} のコンストラクタが見つかりません。");
                    result = ctor.Invoke(ConvertArgs(ctor.GetParameters(), argsRaw));
                }
                else
                {
                    result = InvokeStaticMemberOrProperty(type, methodName!, argsRaw);
                }

                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    result = task.GetType().IsGenericType
                        ? task.GetType().GetProperty("Result")?.GetValue(task) : null;
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
        // 静的メンバー呼び出し
        // ---------------------------------------------------------------------------

        private static object? InvokeStaticMemberOrProperty(Type type, string memberName, object?[] rawArgs)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == memberName)
                .OrderBy(m => m.GetParameters().Length == rawArgs.Length ? 0 : 1)
                .ToList();

            if (methods.Count > 0)
            {
                foreach (var m in methods.Where(m => m.GetParameters().Length == rawArgs.Length))
                {
                    try { return m.Invoke(null, ConvertArgs(m.GetParameters(), rawArgs)); }
                    catch (ArgumentException) { }
                    catch (InvalidCastException) { }
                }

                var noParam = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
                if (noParam != null) return noParam.Invoke(null, Array.Empty<object?>());

                var first = methods[0];
                return first.Invoke(null, ConvertArgs(first.GetParameters(), rawArgs));
            }

            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
            if (prop != null) return prop.GetValue(null);

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
            if (field != null) return field.GetValue(null);

            throw new MissingMethodException($"{type.Name}.{memberName} が見つかりません。");
        }

        // ---------------------------------------------------------------------------
        // 引数変換
        // ---------------------------------------------------------------------------

        private static object?[] ConvertArgs(ParameterInfo[] parameters, object?[] rawArgs)
        {
            var result = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var raw = i < rawArgs.Length ? rawArgs[i] : null;
                result[i] = ConvertArg(raw, parameters[i].ParameterType);
            }
            return result;
        }

        /// <summary>
        /// JavaScriptSerializer がデシリアライズした値を C# の具体的な型に変換する。
        ///
        /// JS → C# で届く値は primitive (string, bool, double, int), ArrayList, Dictionary<string, object> のいずれか。
        /// </summary>
        private static object? ConvertArg(object? raw, Type targetType)
        {
            if (raw == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            // Nullable<T> のアンラップ
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null) targetType = underlying;

            // 型が既に一致
            if (targetType.IsAssignableFrom(raw.GetType())) return raw;

            // --- byte[]: ArrayList（数値配列）または既存 byte[] を受け付ける ---
            if (targetType == typeof(byte[]))
            {
                if (raw is byte[] already) return already;
                if (raw is ArrayList byteArray)
                    return byteArray.Cast<object>().Select(t => Convert.ToByte(t)).ToArray();
                // フォールバック: Base64 文字列
                if (raw is string b64)
                    return Convert.FromBase64String(b64);
            }

            // --- enum: 文字列名または数値から変換 ---
            if (targetType.IsEnum)
            {
                if (raw is string s) return Enum.Parse(targetType, s, ignoreCase: true);
                return Enum.ToObject(targetType, Convert.ToInt64(raw));
            }

            // --- Facepunch.Steamworks 特殊構造体 ---
            if (targetType == typeof(AppId))        return new AppId        { Value = Convert.ToUInt32(raw) };
            if (targetType == typeof(SteamId))      return new SteamId      { Value = Convert.ToUInt64(raw) };
            if (targetType == typeof(DepotId))      return new DepotId      { Value = Convert.ToUInt32(raw) };
            if (targetType == typeof(GameId))       return new GameId       { Value = Convert.ToUInt64(raw) };
            if (targetType == typeof(InventoryDefId))  return new InventoryDefId  { Value = Convert.ToInt32(raw) };
            if (targetType == typeof(InventoryItemId)) return new InventoryItemId { Value = Convert.ToUInt64(raw) };

            // --- IConvertible（int / float / ulong / bool / string など） ---
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
                SteamScreenshots.WriteScreenshot(rgb, width, height);

#if DEBUG
                AppLog.Log("INFO", "SteamBridgeImpl.Screenshot", $"WriteScreenshot 完了 ({width}x{height})");
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

            var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
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
                        payload = s_serializer.Serialize(new Dictionary<string, object>
                        {
                            ["source"]    = "steam",
                            ["messageId"] = "invoke-result",
                            ["error"]     = error,
                            ["asyncId"]   = asyncId,
                        });
                    }
                    else
                    {
                        var wrappedResult = WrapSteamObjects(result);
                        payload = s_serializer.Serialize(new Dictionary<string, object>
                        {
                            ["source"]    = "steam",
                            ["messageId"] = "invoke-result",
                            ["result"]    = wrappedResult,
                            ["asyncId"]   = asyncId,
                        });
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
        /// Steamworks 名前空間のオブジェクトをハンドル参照に変換する。
        /// IEnumerable の要素は再帰的に処理する。
        /// </summary>
        private object? WrapSteamObjects(object? result)
        {
            if (result == null) return null;
            // byte[] は数値配列に変換して JS に渡す
            if (result is byte[] bytes)
            {
                var list = new ArrayList(bytes.Length);
                foreach (var b in bytes) list.Add((int)b);
                return list;
            }

            var type = result.GetType();

            // IEnumerable（string / byte[] を除く）を再帰的にラップ
            if (result is System.Collections.IEnumerable enumerable
                && !(result is string) && !(result is byte[]))
            {
                return enumerable.Cast<object?>().Select(WrapSteamObjects).ToList();
            }

            // Steamworks 名前空間の非 enum オブジェクトはハンドル化
            if (type.Namespace != null && type.Namespace.StartsWith("Steamworks") && !type.IsEnum)
            {
                long id = System.Threading.Interlocked.Increment(ref _nextHandleId);
                _handleRegistry[id] = result;
                return new Dictionary<string, object>
                {
                    ["__isHandle"] = true,
                    ["__handleId"] = id,
                    ["className"]  = type.Name
                };
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
                    var payload = s_serializer.Serialize(new Dictionary<string, object>
                    {
                        ["source"] = "steam",
                        ["event"]  = eventName,
                        ["params"] = eventParams,
                    });

                    _webView.CoreWebView2.PostWebMessageAsString(payload);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "SteamBridgeImpl.PostEventToJs", ex.Message, ex);
                }
            }));
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
