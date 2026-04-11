using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace WebView2AppHost
{
    /// <summary>
    /// app.conf.json の設定値。
    /// Structured app.conf.json を読み込み、ランタイムで扱える形へ正規化する。
    /// </summary>
    [DataContract]
    public sealed class AppConfig
    {
        public static bool IsSecureMode
        {
            get
            {
#if SECURE_OFFLINE
                return true;
#else
                return false;
#endif
            }
        }

        private const int MinSize = 160;
        private const int MaxWidth = 7680;
        private const int MaxHeight = 4320;

        private static readonly Regex s_controlCharRegex =
            new Regex(@"[\p{C}]", RegexOptions.Compiled);

        [DataMember(Name = "title")]
        public string Title { get; private set; } = "WebView2 App Host";

        public int Width { get; set; } = 1280;

        public int Height { get; set; } = 720;

        public bool Fullscreen { get; set; } = false;

        [DataMember(Name = "url")]
        public string Url { get; private set; } = "https://app.local/index.html";

        [DataMember(Name = "window")]
        public WindowConfig? Window { get; private set; }

        [DataMember(Name = "navigation_policy")]
        public NavigationPolicyConfig? NavigationPolicy { get; private set; }

        [DataMember(Name = "connectors")]
        public ConnectorEntry[] Connectors { get; private set; } = Array.Empty<ConnectorEntry>();

        [DataMember(Name = "proxy_origins")]
        public string[] ProxyOrigins { get; private set; } = Array.Empty<string>();

        public string RawJson { get; private set; } = "{}";

        [DataMember(Name = "steam")]
        public SteamConfig? Steam { get; private set; }

        public string SteamAppId => Steam?.AppId ?? "";

        public bool SteamDevMode => Steam?.DevMode ?? true;

        public LoadDllEntry[] LoadDlls { get; private set; } = Array.Empty<LoadDllEntry>();

        public SidecarEntry[] Sidecars { get; private set; } = Array.Empty<SidecarEntry>();

        public bool Frame => Window?.Frame ?? true;

        public string[] OpenInHost => NavigationPolicy?.OpenInHost ?? Array.Empty<string>();

        public string[] OpenInBrowser => NavigationPolicy?.OpenInBrowser ?? Array.Empty<string>();

        public string[] BlockExternalHosts => NavigationPolicy?.Block ?? Array.Empty<string>();

        public string[] AllowedExternalSchemes => NavigationPolicy?.AllowedExternalSchemes ?? Array.Empty<string>();

        public string ExternalNavigationMode => string.IsNullOrWhiteSpace(NavigationPolicy?.ExternalNavigationMode)
            ? ""
            : NavigationPolicy!.ExternalNavigationMode.Trim();

        public string[] BlockRequestPatterns => NavigationPolicy?.BlockRequestPatterns ?? Array.Empty<string>();

        private Regex[]? _openInHostRegexes;
        private Regex[]? _openInBrowserRegexes;
        private Regex[]? _blockExternalHostsRegexes;
        private Regex[]? _allowedExternalSchemesRegexes;
        private Regex[]? _blockRequestPatternsRegexes;

        public bool IsProxyAllowed(Uri uri)
        {
            if (ProxyOrigins == null || ProxyOrigins.Length == 0) return false;
            var origin = uri.Scheme + "://" + uri.Host
                + (uri.IsDefaultPort ? "" : ":" + uri.Port);
            return ProxyOrigins.Any(o =>
                string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));
        }

        public bool ShouldOpenInHost(string host)
            => MatchesAny(host, _openInHostRegexes);

        public bool ShouldOpenInBrowser(string host)
            => MatchesAny(host, _openInBrowserRegexes);

        public bool IsExternalHostBlocked(string host)
            => MatchesAny(host, _blockExternalHostsRegexes);

        public bool IsExternalSchemeAllowed(string scheme)
            => MatchesAny(scheme, _allowedExternalSchemesRegexes);

        public bool IsRequestBlocked(string target)
            => MatchesAny(target, _blockRequestPatternsRegexes);

        public static AppConfig? Load(Stream stream)
        {
            try
            {
                string rawJson;
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
                {
                    rawJson = reader.ReadToEnd();
                }

                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(rawJson));
                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                var config = (AppConfig?)serializer.ReadObject(ms);
                if (config != null)
                {
                    config.RawJson = rawJson;
                    config.Sanitize();
                }
                return config;
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "AppConfig.Load", "設定ファイルの読み込みに失敗（デフォルト値を使用）", ex);
                return null;
            }
        }

        public void ApplyUserConfig(string exeDir)
        {
            var path = Path.Combine(exeDir, "user.conf.json");
            if (!File.Exists(path)) return;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var serializer = new DataContractJsonSerializer(typeof(UserConfig));
                var user = (UserConfig?)serializer.ReadObject(stream);
                if (user == null) return;

                if (user.Width.HasValue)
                    Width = Math.Max(MinSize, Math.Min(user.Width.Value, MaxWidth));
                if (user.Height.HasValue)
                    Height = Math.Max(MinSize, Math.Min(user.Height.Value, MaxHeight));
                if (user.Fullscreen.HasValue)
                    Fullscreen = user.Fullscreen.Value;

                if (Window != null)
                {
                    Window.Width = Width;
                    Window.Height = Height;
                    Window.Fullscreen = Fullscreen;
                }

                AppLog.Log("INFO", "AppConfig.ApplyUserConfig", $"user.conf.json を適用: {AppLog.DescribePath(path)}");
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "AppConfig.ApplyUserConfig", "user.conf.json の読み込みに失敗（無視）", ex);
            }
        }

        private void Sanitize()
        {
            if (string.IsNullOrWhiteSpace(Title))
            {
                Title = "WebView2 App Host";
            }
            else
            {
                Title = s_controlCharRegex.Replace(Title, "").Trim();
                if (Title.Length == 0) Title = "WebView2 App Host";
            }

            Window ??= new WindowConfig();
            Window.Width = NormalizeDimension(Window.Width ?? Width, MaxWidth);
            Window.Height = NormalizeDimension(Window.Height ?? Height, MaxHeight);
            Window.Fullscreen = Window.Fullscreen ?? Fullscreen;
            Window.Frame = Window.Frame ?? true;

            Width = Window.Width.Value;
            Height = Window.Height.Value;
            Fullscreen = Window.Fullscreen.Value;

            Url = string.IsNullOrWhiteSpace(Url) ? "https://app.local/index.html" : Url.Trim();

            ProxyOrigins ??= Array.Empty<string>();
            Steam ??= new SteamConfig();
            Connectors ??= Array.Empty<ConnectorEntry>();

            NavigationPolicy ??= new NavigationPolicyConfig();
            NavigationPolicy.OpenInHost ??= Array.Empty<string>();
            NavigationPolicy.OpenInBrowser ??= Array.Empty<string>();
            NavigationPolicy.Block ??= Array.Empty<string>();
            NavigationPolicy.AllowedExternalSchemes ??= Array.Empty<string>();
            NavigationPolicy.BlockRequestPatterns ??= Array.Empty<string>();
            NavigationPolicy.ExternalNavigationMode = string.IsNullOrWhiteSpace(NavigationPolicy.ExternalNavigationMode)
                ? ""
                : NavigationPolicy.ExternalNavigationMode.Trim().ToLowerInvariant();

            _openInHostRegexes = CompileWildcardPatterns(NavigationPolicy.OpenInHost);
            _openInBrowserRegexes = CompileWildcardPatterns(NavigationPolicy.OpenInBrowser);
            _blockExternalHostsRegexes = CompileWildcardPatterns(NavigationPolicy.Block);
            _allowedExternalSchemesRegexes = CompileWildcardPatterns(NavigationPolicy.AllowedExternalSchemes);
            _blockRequestPatternsRegexes = CompileWildcardPatterns(NavigationPolicy.BlockRequestPatterns);

            NormalizeConnectors();
        }

        private void NormalizeConnectors()
        {
            var dlls = new List<LoadDllEntry>();
            var sidecars = new List<SidecarEntry>();

            foreach (var connector in Connectors)
            {
                if (connector == null || string.IsNullOrWhiteSpace(connector.Type)) continue;

                if (string.Equals(connector.Type, "dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(connector.Path)) continue;

                    var alias = !string.IsNullOrWhiteSpace(connector.Alias)
                        ? connector.Alias
                        : Path.GetFileNameWithoutExtension(connector.Path);

                    if (!dlls.Any(d => string.Equals(d.Alias, alias, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(d.Dll, connector.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        dlls.Add(new LoadDllEntry
                        {
                            Alias = alias,
                            Dll = connector.Path,
                            ExposeEvents = connector.ExposeEvents ?? Array.Empty<string>()
                        });
                    }
                    continue;
                }

                if (string.Equals(connector.Type, "sidecar", StringComparison.OrdinalIgnoreCase))
                {
                    var executable = connector.Executable;
                    var args = connector.Args ?? Array.Empty<string>();

                    if (!string.IsNullOrWhiteSpace(connector.Runtime))
                    {
                        executable = connector.Runtime;
                        if (!string.IsNullOrWhiteSpace(connector.Script))
                            args = (new[] { connector.Script }).Concat(args).ToArray();
                    }

                    if (string.IsNullOrWhiteSpace(executable)) continue;

                    var alias = !string.IsNullOrWhiteSpace(connector.Alias)
                        ? connector.Alias
                        : InferSidecarAlias(connector.Runtime, connector.Script, executable);

                    if (!sidecars.Any(s => string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                    {
                        sidecars.Add(new SidecarEntry
                        {
                            Alias = alias,
                            Mode = string.IsNullOrWhiteSpace(connector.Mode) ? "streaming" : connector.Mode,
                            Executable = executable,
                            WorkingDirectory = connector.WorkingDirectory ?? "",
                            Args = args,
                            Encoding = string.IsNullOrWhiteSpace(connector.Encoding) ? "utf-8" : connector.Encoding,
                            WaitForReady = connector.WaitForReady ?? true
                        });
                    }
                }
            }

            LoadDlls = dlls.ToArray();
            Sidecars = sidecars.ToArray();
        }

        private static int NormalizeDimension(int value, int maxValue)
            => Math.Max(MinSize, Math.Min(value, maxValue));

        private static string InferSidecarAlias(string? runtime, string? script, string executable)
        {
            if (!string.IsNullOrWhiteSpace(runtime))
            {
                var normalized = runtime!.Trim().ToLowerInvariant();
                if (normalized == "node") return "Node";
                if (normalized == "python") return "Python";
                if (normalized == "powershell" || normalized == "pwsh") return "PowerShell";
                return char.ToUpperInvariant(normalized[0]) + normalized.Substring(1);
            }

            if (!string.IsNullOrWhiteSpace(script))
                return Path.GetFileNameWithoutExtension(script);

            return Path.GetFileNameWithoutExtension(executable);
        }

        private static bool MatchesAny(string value, Regex[]? regexes)
        {
            if (regexes == null || regexes.Length == 0 || string.IsNullOrWhiteSpace(value))
                return false;

            return regexes.Any(r => r.IsMatch(value));
        }

        private static Regex[] CompileWildcardPatterns(string[]? patterns)
        {
            if (patterns == null || patterns.Length == 0)
                return Array.Empty<Regex>();

            return patterns
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p =>
                {
                    var pattern = "^" + Regex.Escape(p.Trim())
                        .Replace(@"\*", ".*")
                        .Replace(@"\?", ".") + "$";
                    return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                })
                .ToArray();
        }
    }

    [DataContract]
    internal sealed class UserConfig
    {
        [DataMember(Name = "width")]
        public int? Width { get; private set; }

        [DataMember(Name = "height")]
        public int? Height { get; private set; }

        [DataMember(Name = "fullscreen")]
        public bool? Fullscreen { get; private set; }
    }

    [DataContract]
    public sealed class WindowConfig
    {
        [DataMember(Name = "width")]
        public int? Width { get; set; }

        [DataMember(Name = "height")]
        public int? Height { get; set; }

        [DataMember(Name = "frame")]
        public bool? Frame { get; set; }

        [DataMember(Name = "fullscreen")]
        public bool? Fullscreen { get; set; }
    }

    [DataContract]
    public sealed class NavigationPolicyConfig
    {
        [DataMember(Name = "external_navigation_mode")]
        public string ExternalNavigationMode { get; set; } = "";

        [DataMember(Name = "open_in_host")]
        public string[] OpenInHost { get; set; } = Array.Empty<string>();

        [DataMember(Name = "open_in_browser")]
        public string[] OpenInBrowser { get; set; } = Array.Empty<string>();

        [DataMember(Name = "block")]
        public string[] Block { get; set; } = Array.Empty<string>();

        [DataMember(Name = "allowed_external_schemes")]
        public string[] AllowedExternalSchemes { get; set; } = Array.Empty<string>();

        [DataMember(Name = "block_request_patterns")]
        public string[] BlockRequestPatterns { get; set; } = Array.Empty<string>();
    }

    [DataContract]
    public sealed class SteamConfig
    {
        [DataMember(Name = "app_id")]
        public string AppId { get; set; } = "";

        [DataMember(Name = "dev_mode")]
        public bool DevMode { get; set; } = true;
    }

    [DataContract]
    public sealed class ConnectorEntry
    {
        [DataMember(Name = "type")]
        public string Type { get; set; } = "";

        [DataMember(Name = "alias")]
        public string Alias { get; set; } = "";

        [DataMember(Name = "path")]
        public string Path { get; set; } = "";

        [DataMember(Name = "runtime")]
        public string Runtime { get; set; } = "";

        [DataMember(Name = "script")]
        public string Script { get; set; } = "";

        [DataMember(Name = "executable")]
        public string Executable { get; set; } = "";

        [DataMember(Name = "working_directory")]
        public string WorkingDirectory { get; set; } = "";

        [DataMember(Name = "mode")]
        public string Mode { get; set; } = "";

        [DataMember(Name = "args")]
        public string[] Args { get; set; } = Array.Empty<string>();

        [DataMember(Name = "encoding")]
        public string Encoding { get; set; } = "";

        [DataMember(Name = "wait_for_ready")]
        public bool? WaitForReady { get; set; }

        [DataMember(Name = "expose_events")]
        public string[] ExposeEvents { get; set; } = Array.Empty<string>();
    }

    [DataContract]
    public sealed class LoadDllEntry
    {
        [DataMember(Name = "alias")]
        public string Alias { get; set; } = "";

        [DataMember(Name = "dll")]
        public string Dll { get; set; } = "";

        [DataMember(Name = "exposeEvents")]
        public string[] ExposeEvents { get; set; } = Array.Empty<string>();
    }

    [DataContract]
    public sealed class SidecarEntry
    {
        [DataMember(Name = "alias")]
        public string Alias { get; set; } = "";

        [DataMember(Name = "mode")]
        public string Mode { get; set; } = "streaming";

        [DataMember(Name = "executable")]
        public string Executable { get; set; } = "";

        [DataMember(Name = "workingDirectory")]
        public string WorkingDirectory { get; set; } = "";

        [DataMember(Name = "args")]
        public string[] Args { get; set; } = Array.Empty<string>();

        [DataMember(Name = "encoding")]
        public string Encoding { get; set; } = "utf-8";

        [DataMember(Name = "waitForReady")]
        public bool WaitForReady { get; set; } = false;
    }
}
