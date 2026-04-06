using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// app.conf.json の設定を読み、コネクターを生成して MessageBus に登録する。
    ///
    /// <para>
    /// 旧: PluginManager（プラグインのロードと HandleWebMessage ブロードキャスト）
    /// 新: ConnectorFactory（コネクターの生成と MessageBus への登録のみ）
    ///     ブロードキャストのロジックは MessageBus が担う
    /// </para>
    /// </summary>
    public static class ConnectorFactory
    {
        // -------------------------------------------------------------------
        // パイプ名
        // -------------------------------------------------------------------

        /// <summary>
        /// EXE 名をもとにパイプ名を生成する。
        /// 異なるアプリ（異なる EXE 名）は別々のパイプを使う。
        /// </summary>
        public static string GetPipeName() =>
            $"webview2apphost-{Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule!.FileName!).ToLowerInvariant()}";

        /// <summary>
        /// プロキシ用の本体 EXE パスを返す。
        /// --mcp-proxy と同じ EXE を使う（引数なしで起動すれば本体 GUI になる）。
        /// </summary>
        public static string GetServerExePath() =>
            Process.GetCurrentProcess().MainModule!.FileName!;
        /// <summary>
        /// Mode 2 用（WebView2 あり）の MessageBus を構築する。
        ///
        /// 登録されるコネクター:
        ///   BrowserConnector … WebView2 JS ↔ C#
        ///   DllConnector     … loadDlls に定義された DLL
        ///   SidecarConnector … sidecars に定義されたプロセス（エントリ数ぶん）
        ///   McpConnector     … --mcp フラグがあれば追加
        /// </summary>
        public static (MessageBus bus, McpConnector? mcp) BuildWithBrowser(
            WebView2           webView,
            AppConfig          config,
            bool               enableMcp,
            System.Threading.CancellationToken shutdownToken = default)
        {
            var bus = new MessageBus();

            // 1. BrowserConnector（WebView2 が初期化済みであること）
            var browser = new BrowserConnector(webView);
            bus.Register(browser);

            // 2. DllConnector
            var dll = new DllConnector();
            dll.Initialize(config.RawJson);
            bus.Register(dll);

            // 3. SidecarConnectors（1エントリ = 1コネクター）
            RegisterSidecars(bus, config, shutdownToken);

            // 4. PipeServerConnector（常に登録 - プロキシ接続を受け付ける）
            var pipe = new PipeServerConnector(GetPipeName(), shutdownToken);
            bus.Register(pipe);
            pipe.Start();

            // 5. McpConnector（--mcp フラグ時のみ）
            McpConnector? mcp = null;
            if (enableMcp)
            {
                mcp = new McpConnector(callTimeout: System.TimeSpan.FromSeconds(30));
                mcp.SetBrowser(browser);
                bus.Register(mcp);
            }

            return (bus, mcp);
        }

        /// <summary>
        /// Mode 1 用（WebView2 なし）の MessageBus を構築する。
        ///
        /// 登録されるコネクター:
        ///   DllConnector     … loadDlls に定義された DLL
        ///   SidecarConnector … sidecars に定義されたプロセス
        ///   McpConnector     … 常に登録（Mode 1 は MCP 専用）
        /// </summary>
        public static (MessageBus bus, McpConnector mcp) BuildHeadless(
            AppConfig          config,
            System.Threading.CancellationToken shutdownToken = default)
        {
            var bus = new MessageBus();

            // DllConnector
            var dll = new DllConnector();
            dll.Initialize(config.RawJson);
            bus.Register(dll);

            // SidecarConnectors
            RegisterSidecars(bus, config, shutdownToken);

            // McpConnector（BrowserConnector なし = ブラウザツール非公開）
            var mcp = new McpConnector(callTimeout: System.TimeSpan.FromSeconds(30));
            bus.Register(mcp);

            return (bus, mcp);
        }

        // -------------------------------------------------------------------
        // 内部ヘルパー
        // -------------------------------------------------------------------

        private static void RegisterSidecars(
            MessageBus bus, AppConfig config,
            System.Threading.CancellationToken shutdownToken)
        {
            if (config.Sidecars == null || config.Sidecars.Length == 0) return;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var entry in config.Sidecars)
            {
                if (string.IsNullOrEmpty(entry.Alias) || string.IsNullOrEmpty(entry.Executable))
                {
                    AppLog.Log("WARN", "ConnectorFactory", $"サイドカー設定不完全: alias={entry.Alias}");
                    continue;
                }

                // 実行ファイルパスを解決
                entry.Executable = ResolveExecutablePath(baseDir, entry.Executable)
                    ?? entry.Executable;

                // 作業ディレクトリを解決
                if (string.IsNullOrEmpty(entry.WorkingDirectory))
                    entry.WorkingDirectory = baseDir;
                else if (!Path.IsPathRooted(entry.WorkingDirectory))
                    entry.WorkingDirectory = Path.Combine(baseDir, entry.WorkingDirectory);

                var sidecar = new SidecarConnector(entry, shutdownToken);
                bus.Register(sidecar);
                sidecar.Start();

                AppLog.Log("INFO", "ConnectorFactory",
                    $"SidecarConnector 登録: alias={entry.Alias}, mode={entry.Mode}");
            }
        }

        private static string? ResolveExecutablePath(string baseDir, string executable)
        {
            if (Path.IsPathRooted(executable))
                return File.Exists(executable) ? executable : null;

            var rel = Path.Combine(baseDir, executable);
            if (File.Exists(rel)) return Path.GetFullPath(rel);

            if (!executable.Contains(Path.DirectorySeparatorChar.ToString())
                && !executable.Contains(Path.AltDirectorySeparatorChar.ToString()))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD")
                    .Split(';').Select(e => e.Trim().ToUpperInvariant()).ToArray();
                var hasExt = exts.Any(e => executable.EndsWith(e, StringComparison.OrdinalIgnoreCase));

                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    var full = Path.Combine(dir, executable);
                    if (File.Exists(full)) return Path.GetFullPath(full);
                    if (!hasExt)
                        foreach (var ext in exts)
                        {
                            var withExt = full + ext;
                            if (File.Exists(withExt)) return Path.GetFullPath(withExt);
                        }
                }
            }
            return null;
        }
    }
}
