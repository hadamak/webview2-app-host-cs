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
        private static readonly string[] s_browserConnectorTypes = { "browser" };
        private static readonly string[] s_dllConnectorTypes = { "dll" };
        private static readonly string[] s_sidecarConnectorTypes = { "sidecar" };
        private static readonly string[] s_pipeConnectorTypes = { "pipe", "pipe_server" };
        private static readonly string[] s_mcpConnectorTypes = { "mcp" };

        public static IReadOnlyList<string> GetAvailableConnectorNames(
            AppConfig? config = null,
            bool enableMcp = false)
        {
            var names = new List<string>();

            if (ShouldRegisterBrowser(config))
                names.Add("Browser");

            if (ShouldRegisterDll(config))
                names.Add("Host");

#if !SECURE_OFFLINE
            if (ShouldRegisterSidecars(config) && config?.Sidecars != null)
            {
                foreach (var sidecar in config.Sidecars)
                {
                    if (!string.IsNullOrWhiteSpace(sidecar.Alias))
                        names.Add(sidecar.Alias);
                }
            }

            if (ShouldRegisterPipeServer(config))
                names.Add("PipeServer");

            if (ShouldRegisterMcp(config, enableMcp))
                names.Add("Mcp");
#endif

            return names;
        }

        public static string GetPipeName() =>
            $"webview2apphost-{Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule!.FileName!).ToLowerInvariant()}";

        public static string GetServerExePath() =>
            Process.GetCurrentProcess().MainModule!.FileName!;

        /// <summary>
        /// Mode 2 用（WebView2 あり）の MessageBus を構築する。
        /// </summary>
#if SECURE_OFFLINE
        public static MessageBus BuildWithBrowser(
            WebView2 webView,
            AppConfig config,
            System.Threading.CancellationToken shutdownToken = default)
        {
            var bus = new MessageBus();

            var browser = new BrowserConnector(webView);
            bus.Register(browser);

            if (ShouldRegisterDll(config))
            {
                var dll = new DllConnector();
                dll.Initialize(config);
                bus.Register(dll);
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
            enableMcp = ShouldRegisterMcp(config, enableMcp);

            BrowserConnector? browser = null;
            DllConnector? dll = null;
            PipeServerConnector? pipe = null;
            var sidecarsRegistered = false;

            if (HasStructuredConnectors(config))
            {
                foreach (var entry in config.Connectors)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Type)) continue;

                    if (MatchesType(entry.Type, s_browserConnectorTypes))
                    {
                        if (browser == null)
                        {
                            browser = new BrowserConnector(webView);
                            bus.Register(browser);
                        }
                        continue;
                    }

                    if (MatchesType(entry.Type, s_dllConnectorTypes))
                    {
                        if (dll == null)
                        {
                            dll = new DllConnector();
                            dll.Initialize(config);
                            bus.Register(dll);
                        }
                        continue;
                    }

                    if (MatchesType(entry.Type, s_sidecarConnectorTypes))
                    {
                        if (!sidecarsRegistered)
                        {
                            RegisterSidecars(bus, config, shutdownToken);
                            sidecarsRegistered = true;
                        }
                        continue;
                    }

                    if (MatchesType(entry.Type, s_pipeConnectorTypes))
                    {
                        if (pipe == null)
                        {
                            pipe = new PipeServerConnector(GetPipeName(), shutdownToken);
                            bus.Register(pipe);
                            pipe.Start();
                        }
                        continue;
                    }

                    if (MatchesType(entry.Type, s_mcpConnectorTypes))
                    {
                        enableMcp = true;
                    }
                }
            }
            else
            {
                browser = new BrowserConnector(webView);
                bus.Register(browser);

                if (ShouldRegisterDll(config))
                {
                    dll = new DllConnector();
                    dll.Initialize(config);
                    bus.Register(dll);
                }

                if (ShouldRegisterSidecars(config))
                {
                    RegisterSidecars(bus, config, shutdownToken);
                    sidecarsRegistered = true;
                }

                pipe = new PipeServerConnector(GetPipeName(), shutdownToken);
                bus.Register(pipe);
                pipe.Start();
            }

            if (browser == null)
            {
                browser = new BrowserConnector(webView);
                bus.Register(browser);
            }

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
            AppConfig config,
            System.Threading.CancellationToken shutdownToken = default)
        {
            var bus = new MessageBus();
            var dllRegistered = false;
            var sidecarsRegistered = false;

            if (HasStructuredConnectors(config))
            {
                foreach (var entry in config.Connectors)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Type)) continue;

                    if (MatchesType(entry.Type, s_dllConnectorTypes) && !dllRegistered)
                    {
                        var dll = new DllConnector();
                        dll.Initialize(config);
                        bus.Register(dll);
                        dllRegistered = true;
                        continue;
                    }

                    if (MatchesType(entry.Type, s_sidecarConnectorTypes) && !sidecarsRegistered)
                    {
                        RegisterSidecars(bus, config, shutdownToken);
                        sidecarsRegistered = true;
                    }
                }
            }
            else
            {
                if (ShouldRegisterDll(config))
                {
                    var dll = new DllConnector();
                    dll.Initialize(config);
                    bus.Register(dll);
                    dllRegistered = true;
                }

                if (ShouldRegisterSidecars(config))
                {
                    RegisterSidecars(bus, config, shutdownToken);
                    sidecarsRegistered = true;
                }
            }

            if (!dllRegistered && config.LoadDlls.Length > 0)
            {
                var dll = new DllConnector();
                dll.Initialize(config);
                bus.Register(dll);
            }

            if (!sidecarsRegistered && config.Sidecars.Length > 0)
                RegisterSidecars(bus, config, shutdownToken);

            var mcp = new McpConnector(config, callTimeout: TimeSpan.FromSeconds(30));
            bus.Register(mcp);

            return (bus, mcp);
        }
#endif

#if !SECURE_OFFLINE
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

        private static bool HasStructuredConnectors(AppConfig? config)
            => config?.Connectors != null && config.Connectors.Length > 0;

        private static bool ShouldRegisterBrowser(AppConfig? config)
            => !HasStructuredConnectors(config) || ContainsConnectorType(config, s_browserConnectorTypes);

        private static bool ShouldRegisterDll(AppConfig? config)
            => (config?.LoadDlls.Length ?? 0) > 0 || ContainsConnectorType(config, s_dllConnectorTypes);

        private static bool ShouldRegisterSidecars(AppConfig? config)
            => (config?.Sidecars.Length ?? 0) > 0 || ContainsConnectorType(config, s_sidecarConnectorTypes);

        private static bool ShouldRegisterPipeServer(AppConfig? config)
            => !HasStructuredConnectors(config) || ContainsConnectorType(config, s_pipeConnectorTypes);

        private static bool ShouldRegisterMcp(AppConfig? config, bool enableMcp)
            => enableMcp || ContainsConnectorType(config, s_mcpConnectorTypes);

        private static bool ContainsConnectorType(AppConfig? config, string[] supportedTypes)
        {
            if (!HasStructuredConnectors(config)) return false;

            foreach (var entry in config!.Connectors)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Type)) continue;
                if (MatchesType(entry.Type, supportedTypes)) return true;
            }

            return false;
        }

        private static bool MatchesType(string type, string[] supportedTypes)
        {
            foreach (var supportedType in supportedTypes)
            {
                if (string.Equals(type, supportedType, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
