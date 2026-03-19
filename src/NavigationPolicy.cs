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
        public static Action Classify(string uri)
        {
            // about:blank への遷移は終了処理の合図
            if (uri == "about:blank")
                return Action.MarkClosing;

            // https://app.local/ 以外の http(s) は既定のブラウザで開く
            if (!uri.StartsWith("https://app.local/") &&
                (uri.StartsWith("http://") || uri.StartsWith("https://")))
                return Action.OpenExternal;

            return Action.Allow;
        }
    }
}
