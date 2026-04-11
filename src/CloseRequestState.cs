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
        private enum State
        {
            None = 0,
            InProgress = 1,
            Confirmed = 2
        }

        private int _state = (int)State.None;

        public bool IsClosingConfirmed => System.Threading.Volatile.Read(ref _state) == (int)State.Confirmed;

        public bool IsClosingInProgress => System.Threading.Volatile.Read(ref _state) == (int)State.InProgress;

        public bool IsHostCloseNavigationPending => System.Threading.Volatile.Read(ref _state) == (int)State.InProgress;

        public void BeginHostCloseNavigation()
        {
            System.Threading.Interlocked.CompareExchange(ref _state, (int)State.InProgress, (int)State.None);
        }

        public void CancelHostCloseNavigation()
        {
            System.Threading.Interlocked.CompareExchange(ref _state, (int)State.None, (int)State.InProgress);
        }

        /// <summary>
        /// JS の window.close() など、about:blank ナビゲーションを経由しない
        /// 直接クローズ要求を確定する。
        /// </summary>
        public void ConfirmDirectClose()
        {
            System.Threading.Interlocked.Exchange(ref _state, (int)State.Confirmed);
        }

        public bool ShouldConvertPageCloseRequestToHostClose()
        {
            var s = System.Threading.Volatile.Read(ref _state);
            return s != (int)State.Confirmed && s != (int)State.InProgress;
        }

        public bool TryCompleteCloseNavigation(bool isSuccess)
        {
            if (System.Threading.Volatile.Read(ref _state) != (int)State.InProgress)
                return false;

            if (isSuccess)
            {
                // InProgress -> Confirmed への遷移に成功した場合のみ true を返す
                return System.Threading.Interlocked.CompareExchange(ref _state, (int)State.Confirmed, (int)State.InProgress) == (int)State.InProgress;
            }
            else
            {
                // 失敗時は None に戻す
                System.Threading.Interlocked.CompareExchange(ref _state, (int)State.None, (int)State.InProgress);
                return false;
            }
        }
    }
}
