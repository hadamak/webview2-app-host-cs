using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// 複数のプラグイン DLL を管理し、JS メッセージを各プラグインへブロードキャストする。
    ///
    /// プラグインの検出順序:
    ///   1. app.conf.json の plugins 配列で明示指定されたもの
    ///   2. 未指定の場合は EXE 隣接の WebView2AppHost.*.dll を自動検出
    ///
    /// メッセージルーティング:
    ///   受信したメッセージはすべてのプラグインへブロードキャストする。
    ///   各プラグインは自身の source フィールドと一致しないメッセージを内部で無視する。
    ///
    /// プラグインの初期化:
    ///   ホストは app.conf.json の生の JSON 文字列をプラグインの Initialize(string) に
    ///   そのまま渡す。プラグインは内部で JSON をパースし、必要な情報だけを抽出する。
    ///   ホスト固有の型（AppConfig 等）はプラグインの引数に含めない。
    /// </summary>
    internal sealed class PluginManager : IDisposable
    {
        private readonly List<IHostPlugin> _plugins = new List<IHostPlugin>();
        private bool _disposed;

        private PluginManager() { }

        /// <summary>ロード済みプラグインが存在するか。</summary>
        public bool HasPlugins => _plugins.Count > 0;

        // ---------------------------------------------------------------------------
        // 静的ファクトリ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 設定に基づきプラグインをロードして PluginManager を構築する。
        /// </summary>
        /// <param name="webView">WebView2 コントロール。プラグインのコンストラクタに渡される。</param>
        /// <param name="config">パース済みの AppConfig（プラグイン名の解決に使用）。</param>
        /// <param name="rawConfigJson">app.conf.json の生の JSON 文字列。プラグインの Initialize(string) にそのまま渡される。</param>
        public static PluginManager Create(WebView2 webView, AppConfig config, string rawConfigJson)
        {
            var manager = new PluginManager();

            var names = ResolvePluginNames(config);
            foreach (var name in names)
                manager.TryLoadPlugin(name, webView, rawConfigJson);

            return manager;
        }

        // ---------------------------------------------------------------------------
        // プラグイン名解決
        // ---------------------------------------------------------------------------

        private static string[] ResolvePluginNames(AppConfig config)
        {
            if (config.Plugins != null && config.Plugins.Length > 0)
                return config.Plugins;

            return DiscoverPluginNames();
        }

        /// <summary>
        /// EXE 隣接の WebView2AppHost.{Name}.dll を列挙してプラグイン名を返す。
        /// </summary>
        private static string[] DiscoverPluginNames()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            const string Prefix = "WebView2AppHost.";
            const string Suffix = ".dll";
            var names = new List<string>();

            try
            {
                foreach (var file in Directory.GetFiles(baseDir, "WebView2AppHost.*.dll"))
                {
                    var fileName = Path.GetFileName(file);
                    if (!fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                        || !fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = fileName.Substring(
                        Prefix.Length,
                        fileName.Length - Prefix.Length - Suffix.Length);

                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "PluginManager.DiscoverPluginNames",
                    "プラグイン DLL の列挙に失敗しました", ex);
            }

            return names.ToArray();
        }

        // ---------------------------------------------------------------------------
        // プラグイン読み込み
        // ---------------------------------------------------------------------------

        private void TryLoadPlugin(string pluginName, WebView2 webView, string rawConfigJson)
        {
            // 汎用プラグイン（GenericDllPlugin / GenericSidecarPlugin）
            TryLoadGenericPlugin(pluginName, webView, rawConfigJson);
        }

        /// <summary>
        /// 汎用プラグイン DLL をロードする。
        /// DLL 内で HandleWebMessage(string) を持つ公開クラスを探し、
        /// リフレクション経由でラップする。
        /// </summary>
        private void TryLoadGenericPlugin(string pluginName, WebView2 webView, string rawConfigJson)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dllPath = Path.Combine(baseDir, $"WebView2AppHost.{pluginName}.dll");

            if (!File.Exists(dllPath))
            {
                AppLog.Log("INFO", "PluginManager",
                    $"WebView2AppHost.{pluginName}.dll が見つかりません。スキップします。");
                return;
            }

            try
            {
                var asm = Assembly.LoadFrom(dllPath);
                foreach (var type in asm.GetExportedTypes())
                {
                    if (!type.IsClass || type.IsAbstract) continue;
                    if (!HasHandleWebMessage(type)) continue;

                    // アセンブリ境界を越えた型同一性の問題を回避するため、
                    // IHostPlugin への直接キャストではなくリフレクション経由ラッパーを使う。
                    var instance = Activator.CreateInstance(type, webView)!;

                    // Initialize(string) シグネチャを呼び出す。
                    // app.conf.json の生の JSON 文字列をそのまま渡す。
                    // プラグインは内部で必要なフィールドだけを抽出する。
                    var initWithJson = type.GetMethod(
                        "Initialize",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(string) },
                        null);

                    if (initWithJson != null)
                    {
                        initWithJson.Invoke(instance, new object[] { rawConfigJson });
                    }
                    else
                    {
                        type.GetMethod(
                            "Initialize",
                            BindingFlags.Public | BindingFlags.Instance,
                            null,
                            Type.EmptyTypes,
                            null)
                            ?.Invoke(instance, null);
                    }

                    var wrapper = new ReflectionPluginWrapper(instance, pluginName);
                    _plugins.Add(wrapper);

                    AppLog.Log("INFO", "PluginManager",
                        $"{pluginName} プラグインをロードしました: {type.FullName}");
                    return;
                }
                AppLog.Log("WARN", "PluginManager",
                    $"{dllPath} に HandleWebMessage(string) を持つ公開クラスが見つかりませんでした");
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "PluginManager",
                    $"{pluginName} プラグインのロードに失敗しました", ex);
            }
        }

        /// <summary>
        /// 型が HandleWebMessage(string) メソッドを持つかをダックタイピングで確認する。
        /// アセンブリ境界を越えたインターフェース名照合を避けるための措置。
        /// </summary>
        private static bool HasHandleWebMessage(Type type) =>
            type.GetMethod("HandleWebMessage", new[] { typeof(string) }) != null;

        // ---------------------------------------------------------------------------
        // メッセージブロードキャスト
        // ---------------------------------------------------------------------------

        /// <summary>
        /// WebMessageReceived から転送されたメッセージをすべてのプラグインへブロードキャストする。
        /// 各プラグインは自身の source フィールドと一致しないメッセージを内部で無視する。
        /// </summary>
        public void HandleWebMessage(string webMessageJson)
        {
            if (_disposed) return;

            foreach (var plugin in _plugins)
            {
                try
                {
                    plugin.HandleWebMessage(webMessageJson);
                }
                catch (Exception ex)
                {
                    AppLog.Log("ERROR", "PluginManager.HandleWebMessage",
                        $"[{plugin.PluginName}] メッセージ処理で例外が発生しました: {ex.Message}", ex);
                }
            }
        }

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var plugin in _plugins)
            {
                try { plugin.Dispose(); }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", "PluginManager.Dispose",
                        $"[{plugin.PluginName}] Dispose に失敗しました: {ex.Message}", ex);
                }
            }
            _plugins.Clear();
        }

        // ---------------------------------------------------------------------------
        // 汎用プラグイン用リフレクションラッパー
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 異なるアセンブリからロードされた型をリフレクション経由で IHostPlugin として公開する。
        /// アセンブリ境界を越えた型同一性の問題を回避するため、ダックタイピングで扱う。
        /// </summary>
        private sealed class ReflectionPluginWrapper : IHostPlugin
        {
            private readonly object _impl;
            private readonly string _pluginName;
            private bool _disposed;

            public ReflectionPluginWrapper(object impl, string pluginName)
            {
                _impl       = impl;
                _pluginName = pluginName;
            }

            public string PluginName => _pluginName;

            public void Initialize(string configJson)
            {
                if (_disposed) return;
                var method = _impl.GetType()
                    .GetMethod("Initialize", new[] { typeof(string) });
                if (method != null)
                {
                    method.Invoke(_impl, new object[] { configJson });
                }
            }

            public void HandleWebMessage(string webMessageJson)
            {
                if (_disposed) return;
                _impl.GetType()
                     .GetMethod("HandleWebMessage", new[] { typeof(string) })
                     ?.Invoke(_impl, new object[] { webMessageJson });
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                (_impl as IDisposable)?.Dispose();
            }
        }
    }
}
