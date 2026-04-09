using System;

namespace WebView2AppHost
{
    /// <summary>
    /// WebView2 内のナビゲーション URI を分類するポリシー。
    /// アプリ設定とビルド構成を元に、ホスト内遷移・外部起動・ブロックを決定する。
    /// </summary>
    internal static class NavigationPolicy
    {
        public enum Action
        {
            /// <summary>通常のナビゲーションとして許可する。</summary>
            Allow,

            /// <summary>OS の既定ブラウザまたは既定ハンドラで開く。</summary>
            OpenExternal,

            /// <summary>ホストでも外部でも開かない。</summary>
            Block,
        }

        private static readonly string[] s_standardAllowedSchemes = { "http", "https", "mailto" };

        /// <summary>
        /// アプリ内コンテンツとして扱う URI かどうかを返す。
        /// </summary>
        public static bool IsAppLocalUri(string uri)
            => uri.StartsWith("https://app.local/", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// NewWindowRequested をホスト内の新規 WebView ウィンドウで受けるべき URI かどうかを返す。
        /// </summary>
        public static bool ShouldOpenHostPopup(string uri, AppConfig? config)
            => Classify(uri, config) == Action.Allow
            && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && (IsAppLocalUri(uri)
                || string.Equals(parsed.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase));

        public static Action Classify(string uri)
            => Classify(uri, config: null);

        public static Action Classify(string uri, AppConfig? config)
        {
            if (string.IsNullOrWhiteSpace(uri) ||
                uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                return Action.Allow;

            if (IsAppLocalUri(uri))
                return Action.Allow;

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                return Action.Allow;

            if (AppConfig.IsSecureMode)
                return Action.Block;

            var scheme = parsed.Scheme ?? "";
            if (!IsBuildAllowedScheme(scheme))
                return Action.Block;

            if (config != null &&
                config.AllowedExternalSchemes.Length > 0 &&
                !config.IsExternalSchemeAllowed(scheme))
                return Action.Block;

            var mode = ResolveExternalNavigationMode(config);
            if (mode == ExternalNavigationMode.Block)
                return Action.Block;

            if (!IsHostMatchedScheme(scheme))
                return Action.OpenExternal;

            var host = parsed.Host ?? "";
            if (config != null && config.IsExternalHostBlocked(host))
                return Action.Block;

            switch (mode)
            {
                case ExternalNavigationMode.Host:
                    return Action.Allow;

                case ExternalNavigationMode.Browser:
                    return Action.OpenExternal;

                case ExternalNavigationMode.Rules:
                    if (config != null && config.ShouldOpenInHost(host))
                        return Action.Allow;
                    if (config != null && config.ShouldOpenInBrowser(host))
                        return Action.OpenExternal;
                    return Action.Block;

                default:
                    return Action.Block;
            }
        }

        private static bool IsBuildAllowedScheme(string scheme)
        {
            if (string.IsNullOrWhiteSpace(scheme))
                return false;

            foreach (var allowed in s_standardAllowedSchemes)
            {
                if (string.Equals(allowed, scheme, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsHostMatchedScheme(string scheme)
            => string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);

        private static ExternalNavigationMode ResolveExternalNavigationMode(AppConfig? config)
        {
            var raw = config?.ExternalNavigationMode ?? "";
            if (string.IsNullOrWhiteSpace(raw))
                return AppConfig.IsSecureMode ? ExternalNavigationMode.Block : ExternalNavigationMode.Browser;

            if (string.Equals(raw, "host", StringComparison.OrdinalIgnoreCase))
                return ExternalNavigationMode.Host;
            if (string.Equals(raw, "browser", StringComparison.OrdinalIgnoreCase))
                return ExternalNavigationMode.Browser;
            if (string.Equals(raw, "rules", StringComparison.OrdinalIgnoreCase))
                return ExternalNavigationMode.Rules;
            if (string.Equals(raw, "block", StringComparison.OrdinalIgnoreCase))
                return ExternalNavigationMode.Block;

            return AppConfig.IsSecureMode ? ExternalNavigationMode.Block : ExternalNavigationMode.Browser;
        }

        private enum ExternalNavigationMode
        {
            Host,
            Browser,
            Rules,
            Block,
        }
    }
}
