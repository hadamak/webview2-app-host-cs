using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;


namespace WebView2AppHost
{
    /// <summary>
    /// JS ↔ C# リフレクション・ディスパッチャの共通基底クラス。
    ///
    /// <para>
    /// 以下のロジックを一元管理する。
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
    ///   <item><see cref="SourceName"/>: JS の source フィールドと照合する文字列（"Host" など）</item>
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

        /// <summary>
        /// バスへの送信口。DllConnector が Publish セッターで設定する。
        /// </summary>
        protected Action<string>? _postMessage;

        /// <summary>JS に返したオブジェクトを保持するハンドルレジストリ。</summary>
        protected readonly ConcurrentDictionary<long, object> _handles =
            new ConcurrentDictionary<long, object>();

        private long _nextHandleId = 1;

        /// <summary>Dispose 済みフラグ。サブクラスの Dispose から設定する。</summary>
        protected bool _disposed;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// コネクターとして使う場合（Publish セッターで後から設定）。
        /// プラグイン互換層は削除し、送信口は _postMessage を直接設定する。
        /// </summary>
        protected ReflectionDispatcherBase() { }

        // ---------------------------------------------------------------------------
        // 純粋抽象メンバー
        // ---------------------------------------------------------------------------

        /// <summary>JS の source フィールドと照合する値。例: "Host"</summary>
        protected abstract string SourceName { get; }

        /// <summary>
        /// この戻り値をハンドル参照としてラップすべきかを判定する。
        ///
        /// <para>
        /// Generic: primitive / string / enum 以外すべて true を返す。
        /// </para>
        /// </summary>
        protected abstract bool ShouldWrapAsHandle(object result);

        /// <summary>
        /// params オブジェクトと className / methodName から呼び出し対象の <see cref="Type"/> を解決する。
        ///
        /// <para>
        /// TriggerScreenshot のような特例処理を自前で完結させた場合は <c>null</c> を返す。
        /// null を受け取った <see cref="DispatchJsonRpcRequestAsync"/> はそれ以上の処理を行わない。
        /// </para>
        /// </summary>
        protected abstract Task<object?> ResolveTypeAsync(
            string? source,
            Dictionary<string, object>? paramsObj,
            string className,
            string methodName,
            object?[]  argsRaw,
            object?    asyncId);

        // ---------------------------------------------------------------------------
        // 仮想フック（オーバーライド任意）
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 汎用変換の前に試みるサブクラス固有の型変換。
        /// 変換できた場合は変換後の値を返し、できない場合は <c>null</c> を返して汎用変換へ委ねる。
        ///
        /// <para>
        /// GenericDllPlugin: オーバーライド不要。
        /// </para>
        /// </summary>
        protected virtual object? TryConvertArgExtra(object? raw, Type targetType) => null;

        // ---------------------------------------------------------------------------
        // 共通: HandleWebMessage コア
        // ---------------------------------------------------------------------------

        /// <summary>
        /// JS から届いた JSON-RPC 2.0 メッセージを解析してディスパッチする。
        /// IHostPlugin.HandleWebMessage の実装本体として呼ぶ。
        /// </summary>
        protected void HandleWebMessageCore(string webMessageJson)
        {
            HandleWebMessageCore(webMessageJson, null);
        }

        /// <summary>
        /// パース済みの辞書を使用して、JSON-RPC 2.0 メッセージをディスパッチする。
        /// </summary>
        protected void HandleWebMessageCore(string webMessageJson, Dictionary<string, object>? msg)
        {
            AppLog.Log(
                AppLog.LogLevel.Debug,
                $"{GetType().Name}.HandleWebMessageCore",
                $"Received: {AppLog.DescribeMessageJson(webMessageJson)}",
                dataKind: AppLog.LogDataKind.Sensitive);
            if (_disposed || string.IsNullOrWhiteSpace(webMessageJson)) return;

            try
            {
                if (msg == null)
                {
                    msg = s_json.Deserialize<Dictionary<string, object>>(webMessageJson);
                }
                
                if (msg == null) return;

                // JSON-RPC 2.0 バージョン確認
                if (!msg.TryGetValue("jsonrpc", out var jsonrpcObj) ||
                    !string.Equals(jsonrpcObj?.ToString(), "2.0", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // method フィールド必須
                if (!msg.TryGetValue("method", out var methodObj) || methodObj == null)
                    return;

                var method = methodObj.ToString();
                if (string.IsNullOrEmpty(method)) return;

                // id はリクエストの場合は必須、通知の場合は省略可
                msg.TryGetValue("id", out var id);

                // method 形式: "PluginName.ClassName.MethodName" または "PluginName.EventName"
                var parts = method.Split('.');
                if (parts.Length < 2) return;

                var sourceFromMethod = parts[0];

                // イベント通知（id なし）
                if (id == null)
                {
                    var eventName = string.Join(".", parts.Skip(1));
                    var eventParams = msg.TryGetValue("params", out var pVal) ? pVal : null;
                    OnNotificationReceived(eventName, eventParams);
                    return;
                }

                // SourceName チェック
                // "source" フィールドを優先し、なければ method から抽出した source を使用
                string? sourceFromMsg = null;
                if (msg.TryGetValue("source", out var srcObj))
                    sourceFromMsg = srcObj?.ToString();

                var source = !string.IsNullOrEmpty(sourceFromMsg) ? sourceFromMsg : sourceFromMethod;

                // リクエストdispatch（id あり）
                _ = Task.Run(async () => await DispatchJsonRpcRequestAsync(source, parts, id, msg));
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, $"{GetType().Name}.HandleWebMessage", ex.Message, ex);
            }
        }


        /// <summary>通知メッセージ（id なし）を処理する。</summary>
        protected virtual void OnNotificationReceived(string eventName, object? eventParams)
        {
            AppLog.Log(AppLog.LogLevel.Warn, $"{GetType().Name}.OnNotificationReceived",
                $"未処理の通知: {eventName}");
        }

        /// <summary>JSON-RPC リクエストを処理する共通フロー。</summary>
        protected async Task DispatchJsonRpcRequestAsync(string? source, string[] methodParts, object? id, Dictionary<string, object> msg)
        {
            AppLog.Log(AppLog.LogLevel.Info, $"{GetType().Name}.DispatchJsonRpcRequestAsync", $"source={source}, methodParts=[{string.Join(", ", methodParts)}], id={id}");
            var logCtx = "?";
            try
            {
                var paramsVal = msg.TryGetValue("params", out var pv) ? pv : null;

                // release: ハンドル解放 (method: "Host.release")
                if (methodParts.Last() == "release")
                {
                    if (paramsVal is Dictionary<string, object> relDict && relDict.TryGetValue("handleId", out var hidObj) && hidObj is IConvertible hid)
                    {
                        var handleId = hid.ToInt64(null);
                        _handles.TryRemove(handleId, out _);
#if DEBUG
                        AppLog.Log(AppLog.LogLevel.Info, $"{GetType().Name}.Release", $"ハンドル {handleId} を解放しました");
#endif
                    }
                    SendJsonRpcResult(id, "ok", null);
                    return;
                }

                // インスタンス呼び出し（params に handleId がある場合）
                if (paramsVal is Dictionary<string, object> instDict && instDict.TryGetValue("handleId", out _))
                {
                    var handleId = instDict["handleId"] is IConvertible hcv ? hcv.ToInt64(null) : 0L;
                    if (!_handles.TryGetValue(handleId, out var inst))
                        throw new InvalidOperationException(
                            $"handleId {handleId} が見つかりません。Release 後に使用した可能性があります。");

                    // インスタンス呼び出しは "PluginName.MethodName" 形式（className 不要）
                    var methodName = methodParts.Length >= 2 ? methodParts.Last() : "Unknown";
                    logCtx = $"{inst.GetType().Name}.{methodName}";

                    var instArgs = ExtractArgsFromParams(instDict);
                    var instResult = InvokeInstanceMember(inst, methodName, instArgs);
                    instResult = await UnwrapTaskAsync(instResult);
                    SendJsonRpcResult(id, instResult, null);
                    return;
                }

                // 静的呼び出し
                if (methodParts.Length < 3)
                {
                    // handleId がないのに methodParts < 3 の場合はエラー
                    throw new ArgumentException("静的呼び出しには PluginName.ClassName.MethodName 形式が必要です。");
                }

                var className = methodParts[1];
                var staticMethodName = methodParts[2];
                logCtx = $"{className}.{staticMethodName}";

                var staticArgs = ExtractArgsFromParams(paramsVal);

                var resolved = await ResolveTypeAsync(source, paramsVal as Dictionary<string, object>, className, staticMethodName, staticArgs, id);
                if (resolved == null || staticMethodName == null) return;

                object? result;
                if (resolved is Type type)
                {
                    // 静的呼び出し
                    result = (staticMethodName == ".ctor" || staticMethodName == "Create")
                        ? InvokeConstructor(type, staticArgs)
                        : InvokeStaticMember(type, staticMethodName, staticArgs);
                }
                else
                {
                    // インスタンス（シングルトン等）呼び出し
                    result = InvokeInstanceMember(resolved, staticMethodName, staticArgs);
                }

                result = await UnwrapTaskAsync(result);
                SendJsonRpcResult(id, result, null);
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                AppLog.Log(AppLog.LogLevel.Error, $"{GetType().Name}.Dispatch[{logCtx}]", inner.Message, inner);
                SendJsonRpcResult(id, null, inner.Message);
            }
        }

        private static object?[] ExtractArgs(Dictionary<string, object>? pDict)
        {
            if (pDict == null || !pDict.TryGetValue("args", out var argsVal) || argsVal == null)
                return Array.Empty<object?>();

            if (argsVal is ArrayList arr)
                return arr.Cast<object?>().ToArray();

            return new[] { argsVal };
        }

        private static object?[] ExtractArgsFromParams(object? paramsVal)
        {
            if (paramsVal is Dictionary<string, object> pDict)
            {
                // "args" キーがあればそれを使う（従来の挙動）
                if (pDict.TryGetValue("args", out var argsVal) && argsVal != null)
                {
                    if (argsVal is ArrayList arr) return arr.Cast<object?>().ToArray();
                    return new[] { argsVal };
                }
                
                // "args" キーがなく、他のキーがある場合（名前付き引数など）
                // 辞書の値を配列に変換してフォールバックを試みる
                if (pDict.Count > 0)
                {
                    // handleId は上流で処理済みだが、防御的にフィルタして誤送信を防ぐ
                    return pDict
                        .Where(kv => !string.Equals(kv.Key, "handleId", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => (object?)kv.Value)
                        .ToArray();
                }
            }
            
            if (paramsVal is ArrayList arr2)
                return arr2.Cast<object?>().ToArray();
                
            return Array.Empty<object?>();
        }

        // ---------------------------------------------------------------------------
        // 共通: ディスパッチャ（テンプレートメソッド）
        // ---------------------------------------------------------------------------


        // ---------------------------------------------------------------------------
        // 共通: メンバー呼び出し
        // ---------------------------------------------------------------------------

        /// <summary>
        /// インスタンスのメソッド / プロパティ / フィールドをこの順で検索して呼び出す。
        /// オーバーロードが存在する場合は引数の数が一致するものを優先する。
        /// </summary>
        protected object? InvokeInstanceMember(object inst, string memberName, object?[] rawArgs)
        {
            var t = inst.GetType();
            const BindingFlags Flags =
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            var methods = t.GetMethods(Flags)
                .Where(m => m.Name == memberName)
                .OrderBy(m => m.GetParameters().Length == rawArgs.Length ? 0 : 1)
                .ToList();

            if (methods.Count > 0)
            {
                // 引数の数が一致するオーバーロードを優先
                foreach (var m in methods.Where(m => m.GetParameters().Length == rawArgs.Length))
                {
                    try { return m.Invoke(inst, ConvertArgs(m.GetParameters(), rawArgs)); }
                    catch (ArgumentException)    { /* 次のオーバーロードへ */ }
                    catch (InvalidCastException) { /* 次のオーバーロードへ */ }
                }
                // 引数数が一致しなければ最初のもので試みる
                var first = methods[0];
                return first.Invoke(inst, ConvertArgs(first.GetParameters(), rawArgs));
            }

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

            // 重要: WebView2 (STA) の継続スレッドを維持するため ConfigureAwait(false) は使わない。
            await task;

            var taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                var val = taskType.GetProperty("Result")?.GetValue(task);
                // 内部型 VoidTaskResult をチェック (Task<VoidTaskResult> 等)
                if (val != null && val.GetType().Name == "VoidTaskResult") return null;
                return val;
            }

            return null; // 非ジェネリック Task は値を返さない
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

        /// <summary>JSON-RPC 2.0 正常応答またはエラー応答を送信する。</summary>
        protected void SendJsonRpcResult(object? id, object? result, string? errorMessage)
        {
            AppLog.Log(
                AppLog.LogLevel.Debug,
                $"{GetType().Name}.SendJsonRpcResult",
                $"id={id}, result={AppLog.DescribeResultSummary(result)}, error={errorMessage}",
                dataKind: AppLog.LogDataKind.Sensitive);
            if (_disposed) return;

            try
            {
                Dictionary<string, object?> payload;
                if (errorMessage != null)
                {
                    payload = new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"]      = id,
                        ["error"]   = new Dictionary<string, object>
                        {
                            ["code"]    = -32000,
                            ["message"] = errorMessage,
                        },
                    };
                }
                else
                {
                    payload = new Dictionary<string, object?>
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"]      = id,
                        ["result"]  = WrapResult(result),
                    };
                }
                _postMessage?.Invoke(s_json.Serialize(payload));
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, $"{GetType().Name}.SendJsonRpcResult", ex.Message, ex);
            }
        }


        /// <summary>
        /// イベント通知メッセージ（JSON-RPC 2.0 通知）を JS へ送信する。
        /// 通知には id を含めない（応答を期待しない）。
        /// プラグインからのコールバック（OnAchievementProgress など）の通知に使用する。
        /// </summary>
        protected void PostEventToJs(string eventName, object eventParams)
        {
            if (_disposed) return;

            try
            {
                var payload = s_json.Serialize(new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["method"]  = $"{SourceName}.{eventName}",
                    ["params"]  = eventParams,
                });
                _postMessage?.Invoke(payload);
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, $"{GetType().Name}.PostEventToJs", ex.Message, ex);
            }
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
                    AppLog.Log(AppLog.LogLevel.Warn, $"{GetType().Name}.DisposeHandles",
                        $"ハンドルオブジェクトの Dispose に失敗: {ex.Message}");
                }
            }
            _handles.Clear();
        }
    }
}
