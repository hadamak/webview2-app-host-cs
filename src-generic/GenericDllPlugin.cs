using System;
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
        /// "dllName" からアセンブリを特定し、"className" で Type を検索する。
        /// GenericDllPlugin に特例処理はないため、常に Type を返す（null は返さない）。
        /// </summary>
        protected override Task<Type?> ResolveTypeAsync(
            Dictionary<string, object> paramsObj,
            string className, string methodName,
            object?[] argsRaw, double asyncId)
        {
            var dllName = paramsObj.TryGetValue("dllName", out var dn) ? dn?.ToString() : null;

            if (string.IsNullOrEmpty(dllName))
                throw new ArgumentException(
                    "dllName が空です。loadDlls で指定したエイリアスを params.dllName に渡してください。");

            if (!_assemblies.TryGetValue(dllName!, out var asm))
                throw new TypeLoadException(
                    $"DLL エイリアス '{dllName}' はロードされていません。" +
                    $" app.conf.json の loadDlls を確認してください。");

            var type = ResolveType(asm, className)
                ?? throw new TypeLoadException(
                    $"クラス '{className}' が {asm.GetName().Name} の公開型に見つかりません。");

            return Task.FromResult<Type?>(type);
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
        /// PluginManager から渡される設定ディレクトリを受け取り、
        /// app.conf.json の "loadDlls" に基づいて DLL をロードする。
        ///
        /// PluginManager.TryLoadGenericPlugin が Initialize(string) シグネチャを検出した場合に呼ばれる。
        /// </summary>
        public void Initialize(string configDir)
        {
            var configPath = Path.Combine(configDir, "app.conf.json");
            if (!File.Exists(configPath))
            {
                AppLog.Log("WARN", "GenericDllPlugin.Initialize",
                    $"app.conf.json が見つかりません: {configPath}");
                return;
            }

            try
            {
                var raw  = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
                var conf = s_json.Deserialize<Dictionary<string, object>>(raw);
                if (conf == null || !conf.TryGetValue("loadDlls", out var loadDllsVal)) return;

                if (!(loadDllsVal is System.Collections.ArrayList itemList) || itemList.Count == 0)
                    return;

                foreach (var item in itemList)
                    TryLoadDllEntry(configDir, item);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericDllPlugin.Initialize",
                    $"loadDlls の読み込みに失敗: {ex.Message}", ex);
            }
        }

        public void HandleWebMessage(string webMessageJson)
            => HandleWebMessageCore(webMessageJson);

        // ---------------------------------------------------------------------------
        // DLL ロードヘルパー
        // ---------------------------------------------------------------------------

        /// <summary>
        /// loadDlls 配列の 1 エントリを解析してアセンブリをロードする。
        /// 文字列（ファイル名のみ）または { "alias": "...", "dll": "..." } オブジェクトを受け付ける。
        /// </summary>
        private void TryLoadDllEntry(string baseDir, object? item)
        {
            string? dllFileName = null;
            string? alias       = null;

            if (item is string s)
            {
                dllFileName = s;
                alias       = Path.GetFileNameWithoutExtension(s);
            }
            else if (item is Dictionary<string, object> d)
            {
                dllFileName = d.TryGetValue("dll",   out var dv) ? dv?.ToString() : null;
                alias       = d.TryGetValue("alias", out var av) ? av?.ToString() : null;
                if (dllFileName != null && alias == null)
                    alias = Path.GetFileNameWithoutExtension(dllFileName);
            }

            if (string.IsNullOrEmpty(dllFileName) || string.IsNullOrEmpty(alias))
            {
                AppLog.Log("WARN", "GenericDllPlugin.TryLoadDllEntry",
                    $"loadDlls エントリを解析できませんでした: {s_json.Serialize(item)}");
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
                _assemblies[alias!] = Assembly.LoadFrom(dllPath);
                AppLog.Log("INFO", "GenericDllPlugin",
                    $"DLL をロードしました: alias={alias}, path={dllPath}");
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericDllPlugin.TryLoadDllEntry",
                    $"DLL のロードに失敗: {dllPath}", ex);
            }
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
            DisposeHandles();
            _assemblies.Clear();
        }
    }
}
