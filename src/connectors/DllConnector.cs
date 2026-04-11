using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WebView2AppHost
{
    /// <summary>
    /// app.conf.json の loadDlls に列挙されたアセンブリをロードし、
    /// JS / MCP から静的・インスタンスメソッドを呼び出せるようにするコネクター。
    ///
    /// <para>
    /// 旧: GenericDllPlugin.dll（外部 DLL として分離）
    /// 新: メイン EXE 内のコネクター（アセンブリ境界問題が消える）
    /// </para>
    ///
    /// ルーティング:
    ///   source = "Host" または DLL エイリアス名 のメッセージを処理する。
    /// </summary>
    public sealed class DllConnector : ReflectionDispatcherBase, IConnector
    {
        // -------------------------------------------------------------------
        // フィールド
        // -------------------------------------------------------------------

        private readonly object _lock = new object();

        private readonly Dictionary<string, Assembly> _assemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        private readonly List<(object? target, EventInfo evt, Delegate handler)> _eventSubscriptions =
            new List<(object?, EventInfo, Delegate)>();

        // -------------------------------------------------------------------
        // IConnector
        // -------------------------------------------------------------------

        public string Name => "Host";

        /// <summary>MessageBus が登録時に設定する。これが ReflectionDispatcherBase の送信口になる。</summary>
        public Action<string> Publish
        {
            set => _postMessage = value;
        }

        public void Deliver(string messageJson, Dictionary<string, object>? messageDict)
        {
            if (_disposed || string.IsNullOrWhiteSpace(messageJson)) return;

            try
            {
                var dict = messageDict ?? s_json.Deserialize<Dictionary<string, object>>(messageJson);
                if (dict == null) return;

                string? source = ExtractSource(dict);
                if (source == null) return;

                // DLL エイリアス宛のメッセージ、または SourceName ("Host") 宛を処理
                bool isDllAlias;
                lock (_lock)
                {
                    isDllAlias = !string.Equals(source, SourceName, StringComparison.OrdinalIgnoreCase)
                        && _assemblies.ContainsKey(source);
                }

                if (isDllAlias || string.Equals(source, SourceName, StringComparison.OrdinalIgnoreCase))
                    HandleWebMessageCore(messageJson, dict);
            }
            catch { /* フィルタ段階の例外は無視 */ }
        }

        // -------------------------------------------------------------------
        // 初期化
        // -------------------------------------------------------------------

        /// <summary>app.conf.json の JSON 文字列から loadDlls を読み込む。</summary>
        public void Initialize(string configJson)
        {
            try
            {
                var conf = s_json.Deserialize<Dictionary<string, object>>(configJson);
                if (conf == null || !conf.TryGetValue("loadDlls", out var raw)
                    || !(raw is System.Collections.ArrayList list) || list.Count == 0)
                {
                    AppLog.Log(AppLog.LogLevel.Info, "DllConnector", "loadDlls が空です");
                    return;
                }
                LoadDllEntries(list.Cast<object>());
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, "DllConnector.Initialize", ex.Message, ex);
            }
        }

        /// <summary>正規化済み AppConfig から loadDlls を読み込む。</summary>
        public void Initialize(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.LoadDlls == null || config.LoadDlls.Length == 0)
            {
                AppLog.Log(AppLog.LogLevel.Info, "DllConnector", "loadDlls が空です");
                return;
            }

            LoadDllEntries(config.LoadDlls.Cast<object>());
        }

        // -------------------------------------------------------------------
        // ReflectionDispatcherBase 実装
        // -------------------------------------------------------------------

        protected override string SourceName => "Host";

        protected override bool ShouldWrapAsHandle(object result)
        {
            var t = result.GetType();
            return !t.IsPrimitive && !(result is string) && !t.IsEnum;
        }

        protected override Task<object?> ResolveTypeAsync(
            string? source, Dictionary<string, object>? p, string className, string methodName,
            object?[] argsRaw, object? id)
        {
            // source (エイリアス名) が指定されていれば優先、なければ className をエイリアスとして試す
            string? dllAlias = source;
            if (string.IsNullOrEmpty(dllAlias) || string.Equals(dllAlias, SourceName, StringComparison.OrdinalIgnoreCase))
                dllAlias = className;

            if (string.IsNullOrEmpty(dllAlias)) return Task.FromResult<object?>(null);

            Assembly? asm;
            lock (_lock)
            {
                _assemblies.TryGetValue(dllAlias!, out asm);
            }

            if (asm == null)
            {
                AppLog.Log(AppLog.LogLevel.Warn, "DllConnector.ResolveType", $"アセンブリが見つかりません: {dllAlias}");
                return Task.FromResult<object?>(null);
            }

            var type = asm.GetType(className, false, true)
                ?? asm.GetExportedTypes()
                    .FirstOrDefault(t => string.Equals(t.Name, className, StringComparison.OrdinalIgnoreCase));

            if (type == null)
                AppLog.Log(AppLog.LogLevel.Warn, "DllConnector.ResolveType", $"型が見つかりません: {className}");

            return Task.FromResult<object?>(type);
        }

        // -------------------------------------------------------------------
        // DLL ロード
        // -------------------------------------------------------------------

        private void LoadDllEntries(IEnumerable<object> entries)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var item in entries)
                TryLoadDllEntry(baseDir, item);
        }

        private void TryLoadDllEntry(string baseDir, object? item)
        {
            string? dllFileName = null;
            string? alias = null;
            string[]? exposeEvents = null;

            if (item is string s)
            {
                dllFileName = s;
                alias = Path.GetFileNameWithoutExtension(s);
            }
            else if (item is Dictionary<string, object> d)
            {
                foreach (var kvp in d)
                {
                    switch (kvp.Key.ToLowerInvariant())
                    {
                        case "dll":   dllFileName = kvp.Value?.ToString(); break;
                        case "alias": alias = kvp.Value?.ToString(); break;
                        case "exposeevents":
                            if (kvp.Value is System.Collections.ArrayList arr)
                                exposeEvents = arr.Cast<object>()
                                    .Select(x => x?.ToString() ?? "").ToArray();
                            break;
                    }
                }
                if (dllFileName != null && alias == null)
                    alias = Path.GetFileNameWithoutExtension(dllFileName);
            }
            else if (item is LoadDllEntry entry)
            {
                dllFileName = entry.Dll;
                alias = string.IsNullOrWhiteSpace(entry.Alias)
                    ? Path.GetFileNameWithoutExtension(entry.Dll)
                    : entry.Alias;
                exposeEvents = entry.ExposeEvents;
            }

            if (string.IsNullOrEmpty(dllFileName) || string.IsNullOrEmpty(alias)) return;

            var dllPath = Path.IsPathRooted(dllFileName!)
                ? dllFileName! : Path.Combine(baseDir, dllFileName!);

            if (!File.Exists(dllPath))
            {
                AppLog.Log(AppLog.LogLevel.Warn, "DllConnector", $"DLL が見つかりません: {dllPath}");
                return;
            }

            try
            {
                var asm = Assembly.LoadFrom(dllPath);
                lock (_lock)
                {
                    _assemblies[alias!] = asm;
                }
                AppLog.Log(AppLog.LogLevel.Info, "DllConnector", $"DLL ロード: alias={alias}, path={dllPath}");

                if (exposeEvents?.Length > 0)
                    SubscribeEvents(asm, alias!, exposeEvents);
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, "DllConnector", $"DLL ロード失敗: {dllPath}: {ex.Message}");
            }
        }

        // -------------------------------------------------------------------
        // イベント購読（DLL の静的イベントを MCP 通知として流す）
        // -------------------------------------------------------------------

        private void SubscribeEvents(Assembly asm, string alias, string[] eventNames)
        {
            foreach (var eventName in eventNames)
            {
                foreach (var type in asm.GetExportedTypes())
                {
                    var evtInfo = type.GetEvent(eventName,
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (evtInfo == null) continue;

                    try
                    {
                        var handlerType = evtInfo.EventHandlerType;
                        if (handlerType == null) continue;

                        var capturedAlias = alias;
                        var capturedEvt   = eventName;
                        var parameters    = handlerType.GetMethod("Invoke")?.GetParameters()
                            ?? Array.Empty<ParameterInfo>();

                        Delegate handler;
                        if (parameters.Length == 0)
                        {
                            var msg = s_json.Serialize(new Dictionary<string, object?>
                                { ["source"] = capturedAlias, ["event"] = capturedEvt, ["params"] = new { } });
                            Action fire = () => _postMessage?.Invoke(msg);
                            handler = Delegate.CreateDelegate(handlerType, fire.Target, fire.Method);
                        }
                        else
                        {
                            handler = CreateGenericEventHandler(handlerType, capturedAlias, capturedEvt, parameters);
                        }

                        if (handler == null) continue;

                        evtInfo.AddEventHandler(null, handler);
                        lock (_lock)
                        {
                            _eventSubscriptions.Add((null, evtInfo, handler));
                        }

                        AppLog.Log(AppLog.LogLevel.Info, "DllConnector",
                            $"イベント購読: {type.Name}.{eventName} (alias={alias})");
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log(AppLog.LogLevel.Warn, "DllConnector.SubscribeEvents",
                            $"イベント {eventName} 購読失敗: {ex.Message}");
                    }
                }
            }
        }

        private Delegate CreateGenericEventHandler(
            Type handlerType, string alias, string eventName, ParameterInfo[] parameters)
        {
            try
            {
                var paramExprs = parameters
                    .Select(p => Expression.Parameter(p.ParameterType, p.Name ?? "arg"))
                    .ToArray();

                var dispatch = GetType().GetMethod(
                    nameof(DispatchDynamicEvent), BindingFlags.NonPublic | BindingFlags.Instance);
                if (dispatch == null) return null!;

                var argsArray = Expression.NewArrayInit(typeof(object),
                    paramExprs.Select(p => Expression.Convert(p, typeof(object))));

                var call = Expression.Call(Expression.Constant(this), dispatch,
                    Expression.Constant(alias),
                    Expression.Constant(eventName),
                    Expression.Constant(parameters.Select(p => p.Name ?? "arg").ToArray()),
                    argsArray);

                return Expression.Lambda(handlerType, call, paramExprs).Compile();
            }
            catch { return null!; }
        }

        private void DispatchDynamicEvent(string alias, string eventName,
            string[] paramNames, object[] args)
        {
            var props = new Dictionary<string, object?>();
            if (args.Length == 1 && args[0] != null)
            {
                var val = args[0];
                if (val.GetType().IsPrimitive || val is string)
                    props["value"] = val;
                else
                    foreach (var p in val.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        try { props[p.Name] = p.GetValue(val); } catch { }
            }
            else if (args.Length == 2 && paramNames[0].ToLower().Contains("sender") && args[1] != null)
            {
                var val = args[1];
                if (val.GetType().IsPrimitive || val is string)
                    props["value"] = val;
                else
                    foreach (var p in val.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        try { props[p.Name] = p.GetValue(val); } catch { }
            }
            else
            {
                for (int i = 0; i < args.Length; i++)
                    props[paramNames[i]] = args[i];
            }

            var json = s_json.Serialize(new Dictionary<string, object?>
                { ["source"] = alias, ["event"] = eventName, ["params"] = props });
            _postMessage?.Invoke(json);
        }

        // -------------------------------------------------------------------
        // ヘルパー
        // -------------------------------------------------------------------

        private static string? ExtractSource(Dictionary<string, object> dict)
        {
            if (dict.TryGetValue("jsonrpc", out var jv)
                && string.Equals(jv?.ToString(), "2.0", StringComparison.OrdinalIgnoreCase))
            {
                if (dict.TryGetValue("method", out var mv) && mv != null)
                {
                    var idx = mv.ToString()!.IndexOf('.');
                    if (idx > 0) return mv.ToString()!.Substring(0, idx);
                }
            }
            else if (dict.TryGetValue("source", out var sv))
            {
                return sv?.ToString();
            }
            return null;
        }

        // -------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                foreach (var (target, evt, handler) in _eventSubscriptions)
                    try { evt.RemoveEventHandler(target, handler); } catch { }
                _eventSubscriptions.Clear();

                DisposeHandles();
                _assemblies.Clear();
            }
        }
    }
}
