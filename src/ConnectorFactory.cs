using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// app.conf.json の設定を読み、コネクターを生成して MessageBus に登録する。
    /// </summary>
    public static class ConnectorFactory
    {
        // -------------------------------------------------------------------
        // パイプ名
        // -------------------------------------------------------------------

        public static string GetPipeName() =>
            $"webview2apphost-{Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule!.FileName!).ToLowerInvariant()}";

        public static string GetServerExePath() =>
            Process.GetCurrentProcess().MainModule!.FileName!;

        /// <summary>
        /// Mode 2 用（WebView2 あり）の MessageBus を構築する。
        /// </summary>
        public static (MessageBus bus, McpConnector? mcp) BuildWithBrowser(
            WebView2           webView,
            AppConfig          config,
            bool               enableMcp,
            System.Threading.CancellationToken shutdownToken = default)
        {
            var bus = new MessageBus();

            // 1. BrowserConnector（WebView2 操作 / ホスト本体ネイティブ機能）
            var browser = new BrowserConnector(webView);
            bus.Register(browser);

            // 2. DllConnector（loadDlls に定義された外部 DLL）
            var dll = new DllConnector();
            dll.Initialize(config.RawJson);
            bus.Register(dll);

            // 4. SidecarConnectors（sidecars に定義された外部プロセス）
            RegisterSidecars(bus, config, shutdownToken);

            // 5. PipeServerConnector（外部プロキシからの接続を受け付ける）
            var pipe = new PipeServerConnector(GetPipeName(), shutdownToken);
            bus.Register(pipe);
            pipe.Start();

            // 6. McpConnector（--mcp フラグ時のみ）
            McpConnector? mcp = null;
            if (enableMcp)
            {
                mcp = new McpConnector(config, callTimeout: TimeSpan.FromSeconds(30));
                mcp.SetBrowser(browser);
                bus.Register(mcp);
            }

            return (bus, mcp);
        }

        /// <summary>
        /// Mode 1 用（WebView2 なし）の MessageBus を構築する。
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

            // McpConnector
            var mcp = new McpConnector(config, callTimeout: TimeSpan.FromSeconds(30));
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

                entry.Executable = ResolveExecutablePath(baseDir, entry.Executable)
                    ?? entry.Executable;

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
