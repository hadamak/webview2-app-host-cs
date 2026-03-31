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
    ///   （SteamBridgeImpl が source: "steam"(大文字小文字不問) のみ処理するのと同じ方式）
    ///
    /// 将来の拡張:
    ///   Node.js 連携など新しいプラグインは WebView2AppHost.{Name}.dll を追加するだけで
    ///   PluginManager に自動検出される。IHostPlugin を実装する型が含まれていれば
    ///   リフレクション経由でロードされる。
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
        ///
        /// <para>
        /// SteamBridge が <c>STEAM_RESTART_REQUIRED</c> を要求する場合は
        /// <see cref="InvalidOperationException"/> をそのまま呼び出し元へ伝播する。
        /// 呼び出し元（<c>App.InitPlugins</c>）でキャッチして <c>Application.Exit()</c> を呼ぶこと。
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// SteamAPI_RestartAppIfNecessary が true を返した場合（メッセージ = "STEAM_RESTART_REQUIRED"）。
        /// </exception>
        public static PluginManager Create(WebView2 webView, AppConfig config)
        {
            var manager = new PluginManager();

            var names = ResolvePluginNames(config);
            foreach (var name in names)
                manager.TryLoadPlugin(name, webView, config);

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

        private void TryLoadPlugin(string pluginName, WebView2 webView, AppConfig config)
        {
            if (pluginName.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            {
                TryLoadSteamPlugin(webView, config);
                return;
            }

            // 汎用プラグイン（将来の拡張用: Node.js など）
            TryLoadGenericPlugin(pluginName, webView);
        }

        /// <summary>
        /// Steam プラグインをロードする。既存の SteamBridge シェルに委譲する。
        /// STEAM_RESTART_REQUIRED は呼び出し元へ伝播する。
        /// </summary>
        private void TryLoadSteamPlugin(WebView2 webView, AppConfig config)
        {
            // SteamBridge.TryCreate は STEAM_RESTART_REQUIRED の場合 InvalidOperationException を投げる。
            // PluginManager.Create の呼び出し元（App.InitPlugins）がキャッチして Application.Exit() を呼ぶ。
            var bridge = SteamBridge.TryCreate(webView, config.SteamAppId, config.SteamDevMode);
            if (bridge == null) return;

            _plugins.Add(bridge);
            AppLog.Log("INFO", "PluginManager", "Steam プラグインをロードしました");
        }

        /// <summary>
        /// 汎用プラグイン DLL をロードする。
        /// DLL 内で IHostPlugin を実装する型（またはリフレクション互換の型）を探す。
        /// </summary>
        private void TryLoadGenericPlugin(string pluginName, WebView2 webView)
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

                    // Initialize(string configDir) シグネチャを優先して試みる。
                    // これにより GenericDllPlugin が app.conf.json の loadDlls を読み込める。
                    // 見つからない場合は引数なし Initialize() にフォールバックする。
                    var initWithDir = type.GetMethod(
                        "Initialize",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(string) },
                        null);

                    if (initWithDir != null)
                    {
                        initWithDir.Invoke(instance, new object[] { baseDir });
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
        /// SteamBridge が ISteamBridgeImpl を object + リフレクションで扱うのと同じ手法。
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
