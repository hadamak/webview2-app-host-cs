using System;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// app.conf.json の "loadDlls" に列挙された任意の DLL を実行時にロードし、
    /// JS から { source:"Host", messageId:"invoke", params:{ dllName, className, methodName, args } }
    /// という形式でメソッドを呼び出せるようにする汎用プラグイン。
    ///
    /// リフレクション・ディスパッチャの共通ロジックは ReflectionDispatcherBase に集約されている。
    /// 本クラスが担うのは DLL ロード / エイリアス解決 / 型検索のみ。
    ///
    /// JS 側の呼び出し例（host.js の Host オブジェクト経由）:
    ///   const rows = await Host.SQLite.Database.QueryAll("SELECT * FROM items");
    ///   const conn = await Host.SQLite.SqliteConnection.Create("test.db");
    ///   await Host.invoke(conn, "Open");
    ///   await Host.invoke(conn, "Release");
    ///
    /// loadDlls フォーマット (app.conf.json):
    ///   // 形式 A: エイリアス = 拡張子除去ファイル名
    ///   "loadDlls": ["SQLite.dll", "MyLogic.dll"]
    ///   // 形式 B: エイリアスを明示
    ///   "loadDlls": [{ "alias": "DB", "dll": "SQLite.dll" }]
    /// </summary>
    public sealed class GenericDllPlugin : ReflectionDispatcherBase, IHostPlugin
    {
        // ---------------------------------------------------------------------------
        // GenericDllPlugin 固有フィールド
        // ---------------------------------------------------------------------------

        /// <summary>エイリアス（大文字小文字不問）→ ロード済みアセンブリ。</summary>
        private readonly Dictionary<string, Assembly> _assemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>イベント購読解除用デリゲートの保持リスト。Dispose 時に解除する。</summary>
        private readonly List<(object target, System.Reflection.EventInfo evt, Delegate handler)> _eventSubscriptions =
            new List<(object, System.Reflection.EventInfo, Delegate)>();

        // ---------------------------------------------------------------------------
        // ReflectionDispatcherBase 実装
        // ---------------------------------------------------------------------------

        protected override string SourceName => "Host";

        /// <summary>
        /// primitive / string / enum 以外のすべてのオブジェクトをハンドル化する。
        /// どのような DLL の戻り値でも参照型・構造体はハンドルとして JS に渡す。
        /// </summary>
        protected override bool ShouldWrapAsHandle(object result)
        {
            var t = result.GetType();
            return !t.IsPrimitive && !(result is string) && !t.IsEnum;
        }

        // TryConvertArgExtra: 汎用 DLL 向け追加変換は不要。
        // 基底クラスの default 実装（null を返す → 汎用変換へフォールバック）をそのまま使う。

        /// <summary>
        /// "dllName"（または className）からアセンブリを特定し、className で Type を検索する。
        /// </summary>
        protected override Task<Type?> ResolveTypeAsync(
            Dictionary<string, object>? p, string className, string methodName,
            object?[] argsRaw, double id)
        {
            AppLog.Log("INFO", "GenericDllPlugin.ResolveTypeAsync", $"className={className}, methodName={methodName}");

            // DLL 名を取得: params に dllName があれば使用、なければ className をエイリアスとして試す
            string? dllName = null;
            if (p != null && p.TryGetValue("dllName", out var dllObj))
            {
                dllName = dllObj?.ToString();
            }

            // params に dllName がない場合は className をエイリアスとして試す
            if (string.IsNullOrEmpty(dllName))
            {
                dllName = className;
            }

            if (string.IsNullOrEmpty(dllName))
            {
                AppLog.Log("WARN", "GenericDllPlugin.ResolveTypeAsync", "dllName が特定できませんでした");
                return Task.FromResult<Type?>(null);
            }

            // アセンブリを検索（className をそのまま試す）
            if (!_assemblies.TryGetValue(dllName!, out var asm))
            {
                // 見つからなければ、Steam なら "Steam" として試す
                if (dllName != "Steam" && _assemblies.TryGetValue("Steam", out var steamAsm))
                {
                    asm = steamAsm;
                    dllName = "Steam";
                }
            }

            if (asm == null)
            {
                AppLog.Log("WARN", "GenericDllPlugin.ResolveTypeAsync", $"アセンブリが見つかりません: {dllName}");
                return Task.FromResult<Type?>(null);
            }

            // 型を検索 - 複数のパターンを試す
            try
            {
                // 1. そのまま試す
                var type = asm.GetType(className, false, true);
                
                // 2. Facepunch.Steamworks.プレフィックスを試す
                if (type == null)
                    type = asm.GetType($"Facepunch.Steamworks.{className}", false, true);
                
                // 3. クラス名だけを確認（静的クラスの可能性）
                if (type == null)
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (string.Equals(t.Name, className, StringComparison.OrdinalIgnoreCase))
                        {
                            type = t;
                            break;
                        }
                    }
                }

                if (type == null)
                {
                    // 型が見つからない場合は、アセンブリ内のすべての型をログ出力
                    AppLog.Log("INFO", "GenericDllPlugin.ResolveTypeAsync", $"アセンブリ内の型をスキャン中...");
                    foreach (var t in asm.GetExportedTypes().Take(20))
                    {
                        AppLog.Log("INFO", "GenericDllPlugin.ResolveTypeAsync", $"  - {t.FullName}");
                    }
                    AppLog.Log("WARN", "GenericDllPlugin.ResolveTypeAsync", $"型が見つかりません: {className}");
                    return Task.FromResult<Type?>(null);
                }

                AppLog.Log("INFO", "GenericDllPlugin.ResolveTypeAsync", $"resolved: {type.FullName}");
                return Task.FromResult<Type?>(type);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericDllPlugin.ResolveTypeAsync", $"型解決エラー: {ex.Message}", ex);
                return Task.FromResult<Type?>(null);
            }
        }

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// PluginManager の汎用ローダーから
        /// Activator.CreateInstance(type, webView) で呼ばれる。
        /// </summary>
        public GenericDllPlugin(WebView2 webView) : base(webView) { }

        // ---------------------------------------------------------------------------
        // IHostPlugin
        // ---------------------------------------------------------------------------

        public string PluginName => "GenericDllPlugin";

        /// <summary>
        /// ホストから app.conf.json の内容を JSON 文字列として受け取り、初期化する。
        /// プラグインは内部で JSON をパースし、"loadDlls" 配列だけを抽出する。
        /// ホストの型（AppConfig など）には一切依存しない。
        /// </summary>
        public void Initialize(string configJson)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            try
            {
                var serializer = new JavaScriptSerializer();
                var conf = serializer.Deserialize<Dictionary<string, object>>(configJson);
                if (conf == null || !conf.TryGetValue("loadDlls", out var loadDllsVal))
                {
                    AppLog.Log("INFO", "GenericDllPlugin.Initialize", "loadDlls が空です");
                    return;
                }

                if (!(loadDllsVal is System.Collections.ArrayList itemList) || itemList.Count == 0)
                {
                    AppLog.Log("INFO", "GenericDllPlugin.Initialize", "loadDlls が空です");
                    return;
                }

                foreach (var item in itemList)
                    TryLoadDllEntry(baseDir, item);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericDllPlugin.Initialize",
                    $"loadDlls の読み込みに失敗: {ex.Message}", ex);
            }
        }

        public void HandleWebMessage(string webMessageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(webMessageJson)) return;

            try
            {
                var dict = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(webMessageJson);
                if (dict == null) return;

                string? source = null;

                // JSON-RPC 2.0 形式の検出
                if (dict.TryGetValue("jsonrpc", out var jsonrpcObj) &&
                    string.Equals(jsonrpcObj?.ToString(), "2.0", StringComparison.OrdinalIgnoreCase))
                {
                    // method フィールドから PluginName を抽出
                    if (dict.TryGetValue("method", out var methodObj) && methodObj != null)
                    {
                        var methodStr = methodObj.ToString();
                        if (!string.IsNullOrEmpty(methodStr))
                        {
                            var dotIdx = methodStr.IndexOf('.');
                            if (dotIdx > 0)
                            {
                                source = methodStr.Substring(0, dotIdx);
                            }
                        }
                    }
                }
                else
                {
                    // legacy 形式: source フィールドを使用
                    if (dict.TryGetValue("source", out var srcObj))
                    {
                        source = srcObj?.ToString();
                    }
                }

                // 登録された DLL アセンブリがあるか確認
                if (source != null && _assemblies.ContainsKey(source))
                {
                    // DLL 名を設定
                    if (dict.TryGetValue("params", out var pObj) && pObj is Dictionary<string, object> pDict)
                    {
                        pDict["dllName"] = source;
                    }
                    
                    // 基底クラス(ReflectionDispatcherBase)が処理できるように source を "Host" に偽装する
                    dict["source"] = "Host";
                    webMessageJson = new JavaScriptSerializer().Serialize(dict);
                    
                    HandleWebMessageCore(webMessageJson);
                    return;
                }

                // DLL に該当しない場合は、他のプラグイン（SidecarPluginなど）に任せる
                // HandleWebMessageCore を呼び出さない
            }
            catch { /* json parse error ignore */ }
        }

        // ---------------------------------------------------------------------------
        // DLL ロードヘルパー
        // ---------------------------------------------------------------------------

        /// <summary>
        /// loadDlls 配列の 1 エントリを解析してアセンブリをロードする。
        /// 文字列（ファイル名のみ）または { "alias": "...", "dll": "...", "exposeEvents": [...] } オブジェクトを受け付ける。
        /// </summary>
        private void TryLoadDllEntry(string baseDir, object? item)
        {
            string? dllFileName = null;
            string? alias       = null;
            string[]? exposeEvents = null;

            if (item is string s)
            {
                dllFileName = s;
                alias       = Path.GetFileNameWithoutExtension(s);
            }
            else if (item is Dictionary<string, object> d)
            {
                foreach (var kvp in d)
                {
                    var key = kvp.Key.ToLowerInvariant();
                    if (key == "dll") dllFileName = kvp.Value?.ToString();
                    else if (key == "alias") alias = kvp.Value?.ToString();
                    else if (key == "exposeevents" && kvp.Value is System.Collections.ArrayList arr)
                        exposeEvents = arr.Cast<object>().Select(x => x?.ToString() ?? "").ToArray();
                }
                
                if (dllFileName != null && alias == null)
                    alias = Path.GetFileNameWithoutExtension(dllFileName);
            }

            if (string.IsNullOrEmpty(dllFileName) || string.IsNullOrEmpty(alias))
            {
                AppLog.Log("WARN", "GenericDllPlugin.TryLoadDllEntry",
                    $"loadDlls エントリを解析できませんでした: {item}");
                return;
            }

            var dllPath = Path.IsPathRooted(dllFileName)
                ? dllFileName!
                : Path.Combine(baseDir, dllFileName!);

            if (!File.Exists(dllPath))
            {
                AppLog.Log("WARN", "GenericDllPlugin.TryLoadDllEntry",
                    $"DLL が見つかりません: {dllPath}");
                return;
            }

            try
            {
                var asm = Assembly.LoadFrom(dllPath);
                _assemblies[alias!] = asm;
                AppLog.Log("INFO", "GenericDllPlugin",
                    $"DLL をロードしました: alias={alias}, path={dllPath}");

                // exposeEvents が指定されている場合、イベントを動的購読する
                if (exposeEvents != null && exposeEvents.Length > 0)
                    SubscribeEvents(asm, alias!, exposeEvents);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericDllPlugin.TryLoadDllEntry",
                    $"DLL のロードに失敗: {dllPath}", ex);
            }
        }

        /// <summary>
        /// アセンブリ内の公開型から指定されたイベントを探索し、動的に購読する。
        /// イベント発火時に PostEventToJs で JS へ通知する。
        /// </summary>
        private void SubscribeEvents(Assembly asm, string alias, string[] eventNames)
        {
            foreach (var eventName in eventNames)
            {
                bool found = false;
                foreach (var type in asm.GetExportedTypes())
                {
                    // 静的イベントを探索
                    var evtInfo = type.GetEvent(eventName,
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (evtInfo == null) continue;

                    try
                    {
                        var handlerType = evtInfo.EventHandlerType;
                        if (handlerType == null) continue;

                        // イベントハンドラのパラメータを取得
                        var invokeMethod = handlerType.GetMethod("Invoke");
                        if (invokeMethod == null) continue;

                        var evtName = eventName; // クロージャ用にキャプチャ
                        var currentAlias = alias; // クロージャ用にキャプチャ

                        // Action / Action<T> / EventHandler / EventHandler<T> に対応する汎用ハンドラを生成
                        var parameters = invokeMethod.GetParameters();
                        Delegate handler;

                        if (parameters.Length == 0)
                        {
                            // Action 型: パラメータなしイベント
                            var evtMsg = new JavaScriptSerializer().Serialize(new Dictionary<string, object?> { ["source"] = currentAlias, ["event"] = evtName, ["params"] = new { } });
                            Action fireAction = () => PostWebMessageAsJson(evtMsg);
                            handler = Delegate.CreateDelegate(handlerType, fireAction.Target, fireAction.Method);
                        }
                        else
                        {
                            // EventHandler<T> またはその他 — ラムダでラップ
                            // sender, args 型のイベントは汎用的に処理
                            var capturedEvtName = evtName;
                            var capturedAlias = currentAlias;
                            handler = CreateGenericEventHandler(handlerType, capturedAlias, capturedEvtName, parameters);
                        }

                        if (handler != null)
                        {
                            // 静的イベントの場合は target = null
                            object? target = evtInfo.GetAddMethod()?.IsStatic == true ? null : null;
                            evtInfo.AddEventHandler(target, handler);
                            _eventSubscriptions.Add((target!, evtInfo, handler));

                            AppLog.Log("INFO", "GenericDllPlugin",
                                $"イベントを購読しました: {type.Name}.{eventName} (alias={alias})");
                            found = true;
                            break; // 最初に見つかった型のイベントを使用
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log("WARN", "GenericDllPlugin.SubscribeEvents",
                            $"イベント {eventName} の購読に失敗: {ex.Message}");
                    }
                }

                if (!found)
                {
                    AppLog.Log("WARN", "GenericDllPlugin.SubscribeEvents",
                        $"イベント '{eventName}' が {asm.GetName().Name} の公開型に見つかりませんでした");
                }
            }
        }

        /// <summary>
        /// 任意の引数を持つイベントデリゲートを System.Linq.Expressions で動的生成する。
        /// イベント発火時に DispatchDynamicEvent にパラメータを転送して JS へ通知する。
        /// </summary>
        private Delegate CreateGenericEventHandler(
            Type handlerType, string alias, string eventName, ParameterInfo[] parameters)
        {
            try
            {
                // 各パラメータの Expression を定義 e.g., (int arg1, string arg2)
                var paramExprs = parameters
                    .Select(p => Expression.Parameter(p.ParameterType, p.Name ?? "arg"))
                    .ToArray();

                // DispatchDynamicEvent(alias, eventName, paramNames, argsArray) の呼び出しを構築
                var dispatchMethod = GetType().GetMethod(nameof(DispatchDynamicEvent), 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (dispatchMethod == null) return null!;

                // alias と eventName の定数
                var aliasConst = Expression.Constant(alias, typeof(string));
                var eventNameConst = Expression.Constant(eventName, typeof(string));

                // paramNames の配列
                var namesArray = Expression.Constant(
                    parameters.Select(p => p.Name ?? "arg").ToArray(), typeof(string[]));
                
                // 引数値を object[] にパック
                // 値型は typeof(object) への Convert（ボックス化）が必要
                var argsArray = Expression.NewArrayInit(typeof(object),
                    paramExprs.Select(p => Expression.Convert(p, typeof(object))));

                // メソッド呼び出しの Expression: this.DispatchDynamicEvent(...)
                var callExpr = Expression.Call(Expression.Constant(this), dispatchMethod,
                    aliasConst, eventNameConst, namesArray, argsArray);

                // ラムダ式の構築し、指定された Delegate 型にコンパイル
                var lambda = Expression.Lambda(handlerType, callExpr, paramExprs);
                return lambda.Compile();
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "GenericDllPlugin.CreateGenericEventHandler",
                    $"動的デリゲートの生成に失敗 (イベント={eventName}, 型={handlerType.Name}): {ex.Message}");
                return null!;
            }
        }

        /// <summary>
        /// 動的生成されたデリゲートから呼び出され、パラメータの解析と JS へのイベント通知を行うハブ。
        /// </summary>
        private void DispatchDynamicEvent(string alias, string eventName, string[] paramNames, object[] args)
        {
            var props = new Dictionary<string, object?>();

            // 引数が1つだけの場合は、プロパティの展開を試みる（従来の EventHandler<T> 等の振る舞い互換のため）
            // ただし args[0] がプリミティブや string の場合は配列や単純な値として送る
            if (args.Length == 1 && args[0] != null)
            {
                var val = args[0];
                var type = val.GetType();
                if (type.IsPrimitive || type == typeof(string))
                {
                    props["value"] = val;
                }
                else
                {
                    // オブジェクトのプロパティを辞書に展開
                    foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        try { props[p.Name] = p.GetValue(val); }
                        catch { /* 無視 */ }
                    }
                }
            }
            // 引数が2つあり、第1引数が sender っぽい名前または object 型で、第2引数が EventArgs 由来の場合
            // 標準的な EventHandler(object sender, EventArgs e) パターンの可能性が高い
            else if (args.Length == 2 && paramNames[0].ToLower().Contains("sender") && args[1] != null)
            {
                var val = args[1];
                var type = val.GetType();
                if (type.IsPrimitive || type == typeof(string))
                {
                    props["value"] = val;
                }
                else
                {
                    foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        try { props[p.Name] = p.GetValue(val); }
                        catch { /* 無視 */ }
                    }
                }
            }
            else if (args.Length > 0)
            {
                // その他の複数引数の場合は、引数名をキーにして全て Dictionary に格納
                for (int i = 0; i < args.Length; i++)
                {
                    props[paramNames[i]] = args[i];
                }
            }

            var msg = new Dictionary<string, object?>
            {
                ["source"] = alias,
                ["event"] = eventName,
                ["params"] = props
            };
            var json = new JavaScriptSerializer().Serialize(msg);
            PostWebMessageAsJson(json);
        }

        /// <summary>
        /// アセンブリ内から名前でクラスを検索する。
        /// 単純名（"DbConnection"）と完全修飾名（"MyLib.Data.DbConnection"）の両方を受け付ける。
        /// </summary>
        private static Type? ResolveType(Assembly asm, string className)
        {
            var direct = asm.GetType(className, throwOnError: false, ignoreCase: true);
            if (direct != null) return direct;

            return asm.GetExportedTypes()
                .FirstOrDefault(t => string.Equals(t.Name, className,
                                                    StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // イベント購読解除
            foreach (var (target, evt, handler) in _eventSubscriptions)
            {
                try { evt.RemoveEventHandler(target, handler); }
                catch { /* 無視 */ }
            }
            _eventSubscriptions.Clear();

            DisposeHandles();
            _assemblies.Clear();
        }

        private void PostWebMessageAsJson(string json)
        {
            if (_disposed) return;
            if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

            _webView.BeginInvoke(new Action(() =>
            {
                if (_disposed || _webView.CoreWebView2 == null) return;
                try
                {
                    _webView.CoreWebView2.PostWebMessageAsString(json);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "GenericDllPlugin.PostWebMessageAsJson", ex.Message, ex);
                }
            }));
        }
    }
}
