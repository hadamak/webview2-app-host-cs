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

            /// <summary>終了処理の開始を示すナビゲーション。</summary>
            MarkClosing,
        }

        /// <summary>
        /// URI を受け取り、アプリがとるべきアクションを返す。
        /// </summary>
        /// <param name="uri">遷移先の URI 文字列。</param>
        /// <param name="isNewWindow">
        /// true のとき NewWindowRequested 経由のナビゲーションとして扱う。
        /// NewWindowRequested では about:blank が合法的に渡される場合があるため、
        /// MarkClosing ではなく OpenExternal（ブロック）として処理する。
        /// </param>
        public static Action Classify(string uri, bool isNewWindow = false)
        {
            // about:blank への遷移は終了処理の合図。
            // ただし NewWindowRequested 経由の場合は終了シグナルではないため除外する。
            if (!isNewWindow && uri == "about:blank")
                return Action.MarkClosing;

            // https://app.local/ 以外の http(s) は既定のブラウザで開く
            if (!uri.StartsWith("https://app.local/") &&
                (uri.StartsWith("http://") || uri.StartsWith("https://")))
                return Action.OpenExternal;

            return Action.Allow;
        }
    }
}
