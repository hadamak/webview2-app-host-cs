using System;

namespace WebView2AppHost
{
    /// <summary>
    /// about:blank 経由の終了シーケンスを追跡する。
    /// キャンセル・失敗時の状態リセットを一箇所にまとめ、App から単体テスト可能にする。
    ///
    /// 状態遷移:
    ///
    ///   [Initial]
    ///     │
    ///     │ BeginHostCloseNavigation()
    ///     ▼
    ///   [InProgress]  ──── CancelHostCloseNavigation() ────▶ [Initial]
    ///     │
    ///     │ TryCompleteCloseNavigation(isSuccess: true)
    ///     ▼
    ///   [Confirmed]  (閉じる処理を進める)
    ///
    ///   ※ TryCompleteCloseNavigation(isSuccess: false) は [Initial] に戻る
    ///      （beforeunload でキャンセルされた場合など）
    ///
    ///   別パス: window.close() などによる直接クローズ要求
    ///   [任意の状態]
    ///     │
    ///     │ ConfirmDirectClose()
    ///     ▼
    ///   [Confirmed]
    ///
    /// IsClosingConfirmed == true になると OnFormClosing が閉じを許可する。
    /// </summary>
    internal sealed class CloseRequestState
    {
        public bool IsClosingConfirmed { get; private set; }

        public bool IsClosingInProgress { get; private set; }

        public bool IsHostCloseNavigationPending { get; private set; }

        public void BeginHostCloseNavigation()
        {
            IsClosingInProgress = true;
            IsHostCloseNavigationPending = true;
        }

        public void CancelHostCloseNavigation()
        {
            IsClosingInProgress = false;
            IsHostCloseNavigationPending = false;
        }

        /// <summary>
        /// JS の window.close() など、about:blank ナビゲーションを経由しない
        /// 直接クローズ要求を確定する。
        /// </summary>
        public void ConfirmDirectClose()
        {
            IsClosingConfirmed = true;
        }

        public bool ShouldConvertPageCloseRequestToHostClose()
        {
            return !IsClosingConfirmed && !IsHostCloseNavigationPending;
        }

        public bool TryCompleteCloseNavigation(bool isSuccess)
        {
            if (!IsHostCloseNavigationPending)
                return false;

            IsClosingInProgress = false;
            IsHostCloseNavigationPending = false;
            if (!isSuccess)
                return false;

            IsClosingConfirmed = true;
            return true;
        }
    }
}
