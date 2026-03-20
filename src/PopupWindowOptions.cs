using System;

namespace WebView2AppHost
{
    /// <summary>
    /// window.open の windowFeatures から受け取ったポップアップウィンドウ設定。
    /// 現状のホストで反映するのはサイズと位置で、UI クローム系フラグは観測用に保持する。
    /// </summary>
    internal sealed class PopupWindowOptions
    {
        public bool HasPosition { get; }

        public int Left { get; }

        public int Top { get; }

        public int Width { get; }

        public int Height { get; }

        public bool ShouldDisplayMenuBar { get; }

        public bool ShouldDisplayStatus { get; }

        public bool ShouldDisplayToolbar { get; }

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
