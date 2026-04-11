using System;
using System.Collections.Generic;
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
        private enum ConnectorKind { Browser, Dll, Sidecar, Pipe, Mcp }

        private static bool MatchesType(string type, ConnectorKind kind)
        {
            switch (kind)
            {
                case ConnectorKind.Browser: return "browser".Equals(type, StringComparison.OrdinalIgnoreCase);
                case ConnectorKind.Dll: return "dll".Equals(type, StringComparison.OrdinalIgnoreCase);
                case ConnectorKind.Sidecar: return "sidecar".Equals(type, StringComparison.OrdinalIgnoreCase);
                case ConnectorKind.Pipe: return "pipe".Equals(type, StringComparison.OrdinalIgnoreCase) || "pipe_server".Equals(type, StringComparison.OrdinalIgnoreCase);
                case ConnectorKind.Mcp: return "mcp".Equals(type, StringComparison.OrdinalIgnoreCase);
                default: return false;
            }
        }

        private static bool IsKindPresent(AppConfig? config, ConnectorKind kind)
        {
            if (config?.Connectors == null) return false;
            return config.Connectors.Any(c => c != null && MatchesType(c.Type, kind));
        }

        public static IReadOnlyList<string> GetAvailableConnectorNames(
            AppConfig? config = null,
            bool enableMcp = false)
        {
            var names = new List<string> { "Browser" };

#if SECURE_OFFLINE
            if (IsKindPresent(config, ConnectorKind.Dll))
                names.Add("Host");
#else
            if (IsKindPresent(config, ConnectorKind.Dll))
                names.Add("Host");

            if (config?.Sidecars != null)
            {
                foreach (var sidecar in config.Sidecars)
                {
                    if (!string.IsNullOrWhiteSpace(sidecar.Alias))
                        names.Add(sidecar.Alias);
                }
            }

            if (IsKindPresent(config, ConnectorKind.Pipe))
                names.Add("PipeServer");

            if (enableMcp || IsKindPresent(config, ConnectorKind.Mcp))
                names.Add("Mcp");
#endif

            return names;
        }

        public static string GetPipeName() =>
            $"webview2apphost-{Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule!.FileName!).ToLowerInvariant()}";

        public static string GetServerExePath() =>
            Process.GetCurrentProcess().MainModule!.FileName!;

#if SECURE_OFFLINE
        public static MessageBus BuildWithBrowser(
            WebView2 webView,
            AppConfig config,
            System.Threading.CancellationToken shutdownToken = default)
        {
            var bus = new MessageBus();

            RegisterBrowser(bus, webView);

            if (IsKindPresent(config, ConnectorKind.Dll))
            {
                RegisterDll(bus, config);
            }

            return bus;
        }
#else
        public static (MessageBus bus, McpConnector? mcp) BuildWithBrowser(
            WebView2 webView,
            AppConfig config,
            bool enableMcp,
            System.Threading.CancellationToken shutdownToken = default)
        {
            var bus = new MessageBus();

            BrowserConnector? browser = null;
            DllConnector? dll = null;
            PipeServerConnector? pipe = null;
            bool sidecarsRegistered = false;

            if (config?.Connectors != null)
            {
                foreach (var entry in config.Connectors)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Type)) continue;

                    if (MatchesType(entry.Type, ConnectorKind.Browser) && browser == null)
                        browser = RegisterBrowser(bus, webView);
                    else if (MatchesType(entry.Type, ConnectorKind.Dll) && dll == null)
                        dll = RegisterDll(bus, config);
                    else if (MatchesType(entry.Type, ConnectorKind.Sidecar) && !sidecarsRegistered)
                    {
                        RegisterSidecars(bus, config, shutdownToken);
                        sidecarsRegistered = true;
                    }
                    else if (MatchesType(entry.Type, ConnectorKind.Pipe) && pipe == null)
                        pipe = RegisterPipe(bus, shutdownToken);
                    else if (MatchesType(entry.Type, ConnectorKind.Mcp))
                        enableMcp = true;
                }
            }

            // フォールバック: Configで指定がなくても最低限Browserは起動する
            if (browser == null)
                browser = RegisterBrowser(bus, webView);

            McpConnector? mcp = null;
            if (enableMcp)
            {
                mcp = new McpConnector(config, callTimeout: TimeSpan.FromSeconds(30));
                mcp.SetBrowser(browser);
                bus.Register(mcp);
            }

            return (bus, mcp);
        }

        public static (MessageBus bus, McpConnector mcp) BuildHeadless(
            AppConfig config,
            System.Threading.CancellationToken shutdownToken = default)
        {
            var bus = new MessageBus();
            bool dllRegistered = false;
            bool sidecarsRegistered = false;

            if (config?.Connectors != null)
            {
                foreach (var entry in config.Connectors)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Type)) continue;

                    if (MatchesType(entry.Type, ConnectorKind.Dll) && !dllRegistered)
                    {
                        RegisterDll(bus, config);
                        dllRegistered = true;
                    }
                    else if (MatchesType(entry.Type, ConnectorKind.Sidecar) && !sidecarsRegistered)
                    {
                        RegisterSidecars(bus, config, shutdownToken);
                        sidecarsRegistered = true;
                    }
                }
            }

            var mcp = new McpConnector(config, callTimeout: TimeSpan.FromSeconds(30));
            bus.Register(mcp);

            return (bus, mcp);
        }
#endif

        private static BrowserConnector RegisterBrowser(MessageBus bus, WebView2 webView)
        {
            var browser = new BrowserConnector(webView);
            bus.Register(browser);
            return browser;
        }

        private static DllConnector RegisterDll(MessageBus bus, AppConfig config)
        {
            var dll = new DllConnector();
            dll.Initialize(config);
            bus.Register(dll);
            return dll;
        }

#if !SECURE_OFFLINE
        private static PipeServerConnector RegisterPipe(MessageBus bus, System.Threading.CancellationToken shutdownToken)
        {
            var pipe = new PipeServerConnector(GetPipeName(), shutdownToken);
            bus.Register(pipe);
            pipe.Start();
            return pipe;
        }

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
                    AppLog.Log(AppLog.LogLevel.Warn, "ConnectorFactory", $"サイドカー設定不完全: alias={entry.Alias}");
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

                AppLog.Log(AppLog.LogLevel.Info, "ConnectorFactory",
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
                    {
                        foreach (var ext in exts)
                        {
                            var withExt = full + ext;
                            if (File.Exists(withExt)) return Path.GetFullPath(withExt);
                        }
                    }
                }
            }
            return null;
        }
#endif
    }
}
