namespace WebView2AppHost
{
    /// <summary>
    /// WebView2 内のナビゲーション URI を分類するポリシー。
    /// App.cs のイベントハンドラから呼び出される純粋ロジック。
    /// </summary>
    internal static class NavigationPolicy
    {
        public enum Action
        {
            /// <summary>通常のナビゲーションとして許可する。</summary>
            Allow,

            /// <summary>OS の既定ブラウザで開く。</summary>
            OpenExternal,
        }

        /// <summary>
        /// アプリ内コンテンツとして扱う URI かどうかを返す。
        /// </summary>
        public static bool IsAppLocalUri(string uri)
            => uri.StartsWith("https://app.local/", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// NewWindowRequested をホスト内の新規 WebView ウィンドウで受けるべき URI かどうかを返す。
        /// </summary>
        public static bool ShouldOpenHostPopup(string uri)
            => IsAppLocalUri(uri);

        /// <summary>
        /// URI を受け取り、アプリがとるべきアクションを返す。
        /// </summary>
        /// <param name="uri">遷移先の URI 文字列。</param>
        public static Action Classify(string uri)
        {
            // https://app.local/ 以外の http(s) は既定のブラウザで開く。
            // NOTE: http://app.local/（http、非 https）への遷移も OpenExternal になる。
            //       これはリダイレクトや設定ミスによる意図しないアクセスを防ぐための
            //       意図的な動作であり、アプリのコンテンツは https://app.local/ のみで提供する。
            if (!IsAppLocalUri(uri) &&
                (uri.StartsWith("http://") || uri.StartsWith("https://")))
                return Action.OpenExternal;

            return Action.Allow;
        }
    }
}
