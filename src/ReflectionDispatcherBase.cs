using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// JS ↔ C# リフレクション・ディスパッチャの共通基底クラス。
    ///
    /// <para>
    /// SteamBridgeImpl と GenericDllPlugin が重複していた以下のロジックを一元管理する。
    /// <list type="bullet">
    ///   <item>HandleWebMessage のパース・source フィルタ・messageId ルーティング</item>
    ///   <item>DispatchInvokeAsync（インスタンス / 静的 / コンストラクタ呼び出しの共通フロー）</item>
    ///   <item>InvokeInstanceMember / InvokeStaticMember / InvokeConstructor</item>
    ///   <item>UnwrapTaskAsync（async メソッドの Task&lt;T&gt; 自動 await）</item>
    ///   <item>ConvertArgs / ConvertArg（JS → C# 型変換、サブクラス拡張フック付き）</item>
    ///   <item>WrapResult（C# → JS 戻り値変換、ハンドル管理）</item>
    ///   <item>SendResult（WebView2 への PostWebMessageAsString）</item>
    ///   <item>DisposeHandles（ハンドルレジストリの IDisposable 解放）</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>サブクラスが実装すること（純粋抽象）</b></para>
    /// <list type="number">
    ///   <item><see cref="SourceName"/>: JS の source フィールドと照合する文字列（"steam" / "Host" など）</item>
    ///   <item><see cref="ShouldWrapAsHandle"/>: 戻り値をハンドル化すべき条件</item>
    ///   <item>
    ///     <see cref="ResolveTypeAsync"/>: params から呼び出し対象の Type を解決する。
    ///     特例処理を自前で完結させた場合は null を返す（ディスパッチャはそれ以上何もしない）。
    ///   </item>
    /// </list>
    ///
    /// <para><b>サブクラスがオーバーライドできること（任意）</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="TryConvertArgExtra"/>: 追加の型変換（null を返すと汎用変換へフォールバック）。
    ///     Steam → AppId / SteamId などの Steamworks 構造体変換をここに実装する。
    ///   </item>
    /// </list>
    /// </summary>
    public abstract class ReflectionDispatcherBase
    {
        // ---------------------------------------------------------------------------
        // 共通フィールド
        // ---------------------------------------------------------------------------

        /// <summary>JS との送受信に使う JSON シリアライザ。サブクラスからも参照可。</summary>
        protected static readonly JavaScriptSerializer s_json = new JavaScriptSerializer();

        protected readonly WebView2 _webView;

        /// <summary>JS に返したオブジェクトを保持するハンドルレジストリ。</summary>
        private readonly ConcurrentDictionary<long, object> _handles =
            new ConcurrentDictionary<long, object>();

        private long _nextHandleId = 1;

        /// <summary>Dispose 済みフラグ。サブクラスの Dispose から設定する。</summary>
        protected bool _disposed;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        protected ReflectionDispatcherBase(WebView2 webView)
        {
            _webView = webView;
        }

        // ---------------------------------------------------------------------------
        // 純粋抽象メンバー
        // ---------------------------------------------------------------------------

        /// <summary>JS の source フィールドと照合する値。例: "steam", "Host"</summary>
        protected abstract string SourceName { get; }

        /// <summary>
        /// この戻り値をハンドル参照としてラップすべきかを判定する。
        ///
        /// <para>
        /// Steam: <c>Steamworks.*</c> 名前空間の非 enum オブジェクトのみ true を返す。<br/>
        /// Generic: primitive / string / enum 以外すべて true を返す。
        /// </para>
        /// </summary>
        protected abstract bool ShouldWrapAsHandle(object result);

        /// <summary>
        /// params オブジェクトと className / methodName から呼び出し対象の <see cref="Type"/> を解決する。
        ///
        /// <para>
        /// TriggerScreenshot のような特例処理を自前で完結させた場合は <c>null</c> を返す。
        /// null を受け取った <see cref="DispatchInvokeAsync"/> はそれ以上の処理を行わない。
        /// </para>
        /// </summary>
        protected abstract Task<Type?> ResolveTypeAsync(
            Dictionary<string, object> paramsObj,
            string className,
            string methodName,
            object?[]  argsRaw,
            double     asyncId);

        // ---------------------------------------------------------------------------
        // 仮想フック（オーバーライド任意）
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 汎用変換の前に試みるサブクラス固有の型変換。
        /// 変換できた場合は変換後の値を返し、できない場合は <c>null</c> を返して汎用変換へ委ねる。
        ///
        /// <para>
        /// Steam: AppId / SteamId / DepotId などの Steamworks 構造体変換をここで実装する。<br/>
        /// Generic: オーバーライド不要。
        /// </para>
        /// </summary>
        protected virtual object? TryConvertArgExtra(object? raw, Type targetType) => null;

        // ---------------------------------------------------------------------------
        // 共通: HandleWebMessage コア
        // ---------------------------------------------------------------------------

        /// <summary>
        /// JS から届いた JSON メッセージを解析してディスパッチする。
        /// IHostPlugin.HandleWebMessage / ISteamBridgeImpl.HandleWebMessage の実装本体として呼ぶ。
        /// source フィールドが <see cref="SourceName"/> と一致しない場合はただちに return する。
        /// </summary>
        protected void HandleWebMessageCore(string webMessageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(webMessageJson)) return;

            try
            {
                var msg = s_json.Deserialize<Dictionary<string, object>>(webMessageJson);
                if (msg == null) return;

                // source フィルタ
                if (!msg.TryGetValue("source", out var srcObj) ||
                    !string.Equals(srcObj?.ToString(), SourceName, StringComparison.OrdinalIgnoreCase))
                    return;

                msg.TryGetValue("messageId", out var midObj);
                var messageId = midObj?.ToString();

                msg.TryGetValue("asyncId", out var asyncIdObj);
                var asyncId = (asyncIdObj is IConvertible cv) ? cv.ToDouble(null) : -1.0;

                // ---- release: ハンドル解放 ----
                if (messageId == "release")
                {
                    if (msg.TryGetValue("params", out var pObj) && pObj is IDictionary pDict
                        && pDict.Contains("handleId") && pDict["handleId"] is IConvertible hid)
                    {
                        _handles.TryRemove(hid.ToInt64(null), out _);
#if DEBUG
                        AppLog.Log("INFO", $"{GetType().Name}.Release",
                            $"ハンドル {hid.ToInt64(null)} を解放しました");
#endif
                    }
                    return;
                }

                if (messageId != "invoke")
                {
                    AppLog.Log("WARN", $"{GetType().Name}.HandleWebMessage",
                        $"未知の messageId: {messageId}");
                    return;
                }

                if (!msg.TryGetValue("params", out var paramsVal)
                    || !(paramsVal is Dictionary<string, object> paramsObj))
                    return;

                Task.Run(async () => await DispatchInvokeAsync(paramsObj, asyncId));
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", $"{GetType().Name}.HandleWebMessage", ex.Message, ex);
            }
        }

        // ---------------------------------------------------------------------------
        // 共通: ディスパッチャ（テンプレートメソッド）
        // ---------------------------------------------------------------------------

        /// <summary>
        /// invoke メッセージを処理する共通フロー。
        ///
        /// <para>
        /// 優先順位:
        /// <list type="number">
        ///   <item>handleId あり → インスタンスメソッド / プロパティ / フィールド</item>
        ///   <item>methodName == ".ctor" / "Create" → コンストラクタ</item>
        ///   <item>静的メソッド（引数数が一致するオーバーロード優先）</item>
        ///   <item>静的プロパティ getter（③で見つからない場合）</item>
        ///   <item>静的フィールド（④で見つからない場合）</item>
        /// </list>
        /// Type の解決は <see cref="ResolveTypeAsync"/> に委譲する。
        /// </para>
        /// </summary>
        protected async Task DispatchInvokeAsync(Dictionary<string, object> p, double asyncId)
        {
            var logCtx = "?";
            try
            {
                var argsRaw = p.TryGetValue("args", out var argsVal) && argsVal is ArrayList arr
                    ? arr.Cast<object?>().ToArray()
                    : Array.Empty<object?>();

                // ---- インスタンス呼び出し（handleId あり） ----
                if (p.TryGetValue("handleId", out var htok) && htok != null)
                {
                    var handleId = (htok is IConvertible hcv) ? hcv.ToInt64(null) : 0L;
                    if (!_handles.TryGetValue(handleId, out var inst))
                        throw new InvalidOperationException(
                            $"handleId {handleId} が見つかりません。Release 後に使用した可能性があります。");

                    var mname = p.TryGetValue("methodName", out var mn) ? mn?.ToString() : null;
                    if (string.IsNullOrEmpty(mname))
                        throw new ArgumentException("インスタンス呼び出しに methodName が指定されていません。");

                    logCtx = $"{inst.GetType().Name}.{mname}";
                    var instResult = InvokeInstanceMember(inst, mname!, argsRaw);
                    instResult = await UnwrapTaskAsync(instResult);
                    SendResult(asyncId, instResult, null);
                    return;
                }

                // ---- 静的呼び出し ----
                var className  = p.TryGetValue("className",  out var cn)  ? cn?.ToString()  : null;
                var methodName = p.TryGetValue("methodName", out var mn2) ? mn2?.ToString() : null;

                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className が空です。");
                if (string.IsNullOrEmpty(methodName))
                    throw new ArgumentException("methodName が空です。");

                logCtx = $"{className}.{methodName}";

                // サブクラスが型を解決する（特例処理を自前で完結した場合は null を返す）
                var type = await ResolveTypeAsync(p, className!, methodName!, argsRaw, asyncId);
                if (type == null) return; // 特例処理済み（TriggerScreenshot など）

                object? result = (methodName == ".ctor" || methodName == "Create")
                    ? InvokeConstructor(type, argsRaw)
                    : InvokeStaticMember(type, methodName!, argsRaw);

                result = await UnwrapTaskAsync(result);
                SendResult(asyncId, result, null);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                AppLog.Log("ERROR", $"{GetType().Name}.Dispatch[{logCtx}]", inner.Message, inner);
                SendResult(asyncId, null, inner.Message);
            }
        }

        // ---------------------------------------------------------------------------
        // 共通: メンバー呼び出し
        // ---------------------------------------------------------------------------

        /// <summary>
        /// インスタンスのメソッド / プロパティ / フィールドをこの順で検索して呼び出す。
        /// </summary>
        protected object? InvokeInstanceMember(object inst, string memberName, object?[] rawArgs)
        {
            var t = inst.GetType();
            const BindingFlags Flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            var method = t.GetMethod(memberName, Flags);
            if (method != null)
                return method.Invoke(inst, ConvertArgs(method.GetParameters(), rawArgs));

            var prop = t.GetProperty(memberName, Flags);
            if (prop != null) return prop.GetValue(inst);

            var field = t.GetField(memberName, Flags);
            if (field != null) return field.GetValue(inst);

            throw new MissingMemberException(
                $"{t.FullName} にメンバー '{memberName}' が見つかりません。");
        }

        /// <summary>
        /// 静的メソッドをオーバーロード解決しながら呼び出す。
        /// メソッドが見つからない場合は静的プロパティ → 静的フィールドの順にフォールバックする。
        /// </summary>
        protected object? InvokeStaticMember(Type type, string memberName, object?[] rawArgs)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Static;

            var methods = type.GetMethods(Flags)
                .Where(m => m.Name == memberName)
                .OrderBy(m => m.GetParameters().Length == rawArgs.Length ? 0 : 1)
                .ToList();

            if (methods.Count > 0)
            {
                // 引数の数が一致するオーバーロードを優先
                foreach (var m in methods.Where(m => m.GetParameters().Length == rawArgs.Length))
                {
                    try { return m.Invoke(null, ConvertArgs(m.GetParameters(), rawArgs)); }
                    catch (ArgumentException)    { /* 次のオーバーロードへ */ }
                    catch (InvalidCastException) { /* 次のオーバーロードへ */ }
                }

                var noParam = methods.FirstOrDefault(m => m.GetParameters().Length == 0);
                if (noParam != null) return noParam.Invoke(null, Array.Empty<object?>());

                var first = methods[0];
                return first.Invoke(null, ConvertArgs(first.GetParameters(), rawArgs));
            }

            var prop = type.GetProperty(memberName, Flags);
            if (prop != null) return prop.GetValue(null);

            var field = type.GetField(memberName, Flags);
            if (field != null) return field.GetValue(null);

            throw new MissingMemberException(
                $"{type.FullName} に静的メンバー '{memberName}' が見つかりません。");
        }

        /// <summary>引数の数が一致するコンストラクタを優先して呼び出す。</summary>
        protected object InvokeConstructor(Type type, object?[] rawArgs)
        {
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (ctors.Length == 0)
                throw new MissingMethodException(
                    $"{type.FullName} に公開コンストラクタが見つかりません。");

            var ctor = ctors
                .OrderBy(c => c.GetParameters().Length == rawArgs.Length ? 0 : 1)
                .First();

            return ctor.Invoke(ConvertArgs(ctor.GetParameters(), rawArgs));
        }

        // ---------------------------------------------------------------------------
        // 共通: Task アンラップ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// result が <see cref="Task"/> であれば await して内部の値を取り出す。
        /// Task（非ジェネリック）の場合は null を返す。
        /// </summary>
        protected static async Task<object?> UnwrapTaskAsync(object? result)
        {
            if (!(result is Task task)) return result;

            await task.ConfigureAwait(false);
            return task.GetType().IsGenericType
                ? task.GetType().GetProperty("Result")?.GetValue(task)
                : null;
        }

        // ---------------------------------------------------------------------------
        // 共通: 引数変換（JS → C#）
        // ---------------------------------------------------------------------------

        /// <summary>ParameterInfo 配列に合わせて rawArgs を変換した配列を返す。</summary>
        protected object?[] ConvertArgs(ParameterInfo[] parameters, object?[] rawArgs)
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
        /// JavaScriptSerializer がデシリアライズした値を C# の具体的な型へ変換する。
        ///
        /// <para>
        /// JS → C# で届く値は次のいずれか:
        /// primitive (string / bool / double / int) / ArrayList / Dictionary&lt;string, object&gt;
        /// </para>
        ///
        /// <para>
        /// 変換の優先順位:
        /// <list type="number">
        ///   <item>型が既に一致</item>
        ///   <item>byte[] 変換（ArrayList or Base64 文字列）</item>
        ///   <item>enum 変換（文字列名 or 数値）</item>
        ///   <item><see cref="TryConvertArgExtra"/>（サブクラス追加変換）</item>
        ///   <item>Convert.ChangeType（IConvertible 汎用変換）</item>
        ///   <item>T.Parse(string)（フォールバック）</item>
        /// </list>
        /// </para>
        /// </summary>
        private object? ConvertArg(object? raw, Type targetType)
        {
            if (raw == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            // Nullable<T> のアンラップ
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null) targetType = underlying;

            // 型が既に一致（または派生型）
            if (targetType.IsAssignableFrom(raw.GetType())) return raw;

            // byte[]: ArrayList（数値配列）または Base64 文字列
            if (targetType == typeof(byte[]))
            {
                if (raw is byte[] already) return already;
                if (raw is ArrayList byteArray)
                    return byteArray.Cast<object>().Select(x => Convert.ToByte(x)).ToArray();
                if (raw is string b64) return Convert.FromBase64String(b64);
            }

            // enum: 文字列名または数値から変換
            if (targetType.IsEnum)
            {
                if (raw is string s) return Enum.Parse(targetType, s, ignoreCase: true);
                return Enum.ToObject(targetType, Convert.ToInt64(raw));
            }

            // サブクラス追加変換（Steam の AppId / SteamId など）
            var extra = TryConvertArgExtra(raw, targetType);
            if (extra != null) return extra;

            // IConvertible 汎用変換
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
        // 共通: 戻り値ラッピング（C# → JS）
        // ---------------------------------------------------------------------------

        /// <summary>
        /// C# の戻り値を JS に渡せる形に変換する。
        ///
        /// <para>
        /// <list type="bullet">
        ///   <item>null → null</item>
        ///   <item>byte[] → int[]（JS が扱いやすい形）</item>
        ///   <item>primitive / string / enum → そのまま</item>
        ///   <item>IEnumerable → Array（要素を再帰ラップ）</item>
        ///   <item><see cref="ShouldWrapAsHandle"/> が true → ハンドルオブジェクト</item>
        ///   <item>それ以外 → そのまま（JavaScriptSerializer に委ねる）</item>
        /// </list>
        /// </para>
        /// </summary>
        protected object? WrapResult(object? result)
        {
            if (result == null) return null;

            // byte[] は int[] として JS に渡す
            if (result is byte[] bytes)
            {
                var list = new ArrayList(bytes.Length);
                foreach (var b in bytes) list.Add((int)b);
                return list;
            }

            var type = result.GetType();

            // primitive / string / enum はそのまま
            if (type.IsPrimitive || result is string || type.IsEnum) return result;

            // IEnumerable は要素を再帰ラップ（string / byte[] は上で処理済み）
            if (result is IEnumerable enumerable)
                return enumerable.Cast<object?>().Select(WrapResult).ToList();

            // サブクラスの判定に従ってハンドル化
            if (ShouldWrapAsHandle(result))
            {
                var id = Interlocked.Increment(ref _nextHandleId);
                _handles[id] = result;
                return new Dictionary<string, object>
                {
                    ["__isHandle"] = true,
                    ["__handleId"] = id,
                    ["className"]  = type.Name,
                };
            }

            return result;
        }

        // ---------------------------------------------------------------------------
        // 共通: JS への送信
        // ---------------------------------------------------------------------------

        /// <summary>invoke-result メッセージを JS へ送信する。</summary>
        protected void SendResult(double asyncId, object? result, string? error)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null) return;
                try
                {
                    // Dictionary の値型は object? — WrapResult は null を返すことがある
                    // (void メソッドや null 戻り値は JSON null として JS に届く)
                    Dictionary<string, object?> payload;
                    if (error != null)
                    {
                        payload = new Dictionary<string, object?>
                        {
                            ["source"]    = SourceName,
                            ["messageId"] = "invoke-result",
                            ["error"]     = error,
                            ["asyncId"]   = asyncId,
                        };
                    }
                    else
                    {
                        payload = new Dictionary<string, object?>
                        {
                            ["source"]    = SourceName,
                            ["messageId"] = "invoke-result",
                            ["result"]    = WrapResult(result),
                            ["asyncId"]   = asyncId,
                        };
                    }
                    _webView.CoreWebView2.PostWebMessageAsString(s_json.Serialize(payload));
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", $"{GetType().Name}.SendResult", ex.Message, ex);
                }
            }));
        }

        /// <summary>
        /// イベント通知メッセージ（invoke-result ではなく任意の event 名）を JS へ送信する。
        /// Steam コールバック（OnAchievementProgress など）の通知に使用する。
        /// </summary>
        protected void PostEventToJs(string eventName, object eventParams)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null) return;
                try
                {
                    var payload = s_json.Serialize(new Dictionary<string, object>
                    {
                        ["source"] = SourceName,
                        ["event"]  = eventName,
                        ["params"] = eventParams,
                    });
                    _webView.CoreWebView2.PostWebMessageAsString(payload);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", $"{GetType().Name}.PostEventToJs", ex.Message, ex);
                }
            }));
        }

        // ---------------------------------------------------------------------------
        // 共通: Dispose ヘルパー
        // ---------------------------------------------------------------------------

        /// <summary>
        /// ハンドルレジストリ内の全オブジェクトを <see cref="IDisposable.Dispose"/> し、
        /// レジストリを空にする。サブクラスの Dispose から呼ぶこと。
        /// </summary>
        protected void DisposeHandles()
        {
            foreach (var obj in _handles.Values)
            {
                try { (obj as IDisposable)?.Dispose(); }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", $"{GetType().Name}.DisposeHandles",
                        $"ハンドルオブジェクトの Dispose に失敗: {ex.Message}");
                }
            }
            _handles.Clear();
        }
    }
}
