using System;

namespace WebView2AppHost
{
    /// <summary>
    /// window.open の windowFeatures から受け取ったポップアップウィンドウ設定。
    /// 現状のホストで反映するのはサイズと位置で、UI クローム系フラグは観測用に保持する。
    /// </summary>
    internal sealed class PopupWindowOptions
    {
        /// <summary>
        /// 位置指定（Left, Top）が明示的に行われているか。
        /// </summary>
        public bool HasPosition { get; }

        /// <summary>
        /// ウィンドウの左端座標。
        /// </summary>
        public int Left { get; }

        /// <summary>
        /// ウィンドウの上端座標。
        /// </summary>
        public int Top { get; }

        /// <summary>
        /// ウィンドウの幅。
        /// </summary>
        public int Width { get; }

        /// <summary>
        /// ウィンドウの高さ。
        /// </summary>
        public int Height { get; }

        /// <summary>
        /// メニューバーを表示すべきか。
        /// </summary>
        public bool ShouldDisplayMenuBar { get; }

        /// <summary>
        /// ステータスバーを表示すべきか。
        /// </summary>
        public bool ShouldDisplayStatus { get; }

        /// <summary>
        /// ツールバーを表示すべきか。
        /// </summary>
        public bool ShouldDisplayToolbar { get; }

        /// <summary>
        /// スクロールバーを表示すべきか。
        /// </summary>
        public bool ShouldDisplayScrollBars { get; }

        private PopupWindowOptions(
            bool hasPosition,
            int left,
            int top,
            int width,
            int height,
            bool shouldDisplayMenuBar,
            bool shouldDisplayStatus,
            bool shouldDisplayToolbar,
            bool shouldDisplayScrollBars)
        {
            HasPosition = hasPosition;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            ShouldDisplayMenuBar = shouldDisplayMenuBar;
            ShouldDisplayStatus = shouldDisplayStatus;
            ShouldDisplayToolbar = shouldDisplayToolbar;
            ShouldDisplayScrollBars = shouldDisplayScrollBars;
        }

        /// <summary>
        /// WebView2 の CoreWebView2NewWindowRequestedEventArgs の各プロパティから PopupWindowOptions を生成する。
        /// </summary>
        public static PopupWindowOptions FromRequestedFeatures(
            bool hasPosition,
            uint left,
            uint top,
            bool hasSize,
            uint width,
            uint height,
            bool shouldDisplayMenuBar,
            bool shouldDisplayStatus,
            bool shouldDisplayToolbar,
            bool shouldDisplayScrollBars,
            int fallbackWidth,
            int fallbackHeight)
        {
            return new PopupWindowOptions(
                hasPosition: hasPosition,
                left: hasPosition ? ClampToInt(left) : 0,
                top: hasPosition ? ClampToInt(top) : 0,
                width: NormalizeDimension(hasSize, width, fallbackWidth),
                height: NormalizeDimension(hasSize, height, fallbackHeight),
                shouldDisplayMenuBar: shouldDisplayMenuBar,
                shouldDisplayStatus: shouldDisplayStatus,
                shouldDisplayToolbar: shouldDisplayToolbar,
                shouldDisplayScrollBars: shouldDisplayScrollBars);
        }

        private static int NormalizeDimension(bool hasRequestedValue, uint requestedValue, int fallbackValue)
        {
            if (!hasRequestedValue || requestedValue == 0)
                return fallbackValue;

            return ClampToInt(requestedValue);
        }

        private static int ClampToInt(uint value)
            => value > int.MaxValue ? int.MaxValue : (int)value;
    }
}
