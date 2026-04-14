using System.Threading;
using System.Threading.Tasks;

namespace WebView2AppHost
{
    /// <summary>
    /// スクリーンショットの結果を格納する。
    /// </summary>
    public sealed class ScreenshotResult
    {
        /// <summary>Base64 エンコードされた PNG 画像データ。</summary>
        public string base64 { get; set; } = "";
        /// <summary>画像の幅（ピクセル）。</summary>
        public int    width  { get; set; }
        /// <summary>画像の高さ（ピクセル）。</summary>
        public int    height { get; set; }
    }

    /// <summary>
    /// McpConnector が browser_* ツールを提供するための最小インターフェース。
    ///
    /// <para>
    /// BrowserConnector が直接実装する場合と、--mcp-proxy モードで
    /// BusBrowserTools（バス経由のプロキシ実装）が実装する場合の両方をサポートする。
    /// これにより McpConnector は WebView2 の型に直接依存しない。
    /// </para>
    /// </summary>
    public interface IBrowserTools
    {
        /// <summary>
        /// WebView2 上で JavaScript を実行し、結果を文字列で返す。
        /// </summary>
        /// <param name="script">実行する JavaScript 式またはステートメント。</param>
        /// <param name="ct">キャンセルトークン。</param>
        /// <returns>JavaScript の評価結果（JSON 文字列）。</returns>
        Task<string> EvaluateAsync(string script, CancellationToken ct = default);

        /// <summary>
        /// WebView2 の現在の表示内容を PNG としてキャプチャし、
        /// Base64 エンコード文字列と画像サイズを返す。
        /// </summary>
        /// <param name="ct">キャンセルトークン。</param>
        /// <returns>スクリーンショット結果。</returns>
        Task<ScreenshotResult> ScreenshotAsync(CancellationToken ct = default);

        /// <summary>
        /// 指定した URL へ WebView2 をナビゲートし、完了まで待機する。
        /// </summary>
        /// <param name="url">ナビゲート先の URL。</param>
        /// <param name="ct">キャンセルトークン。</param>
        Task NavigateAsync(string url, CancellationToken ct = default);

        /// <summary>
        /// WebView2 で現在表示されているページの URL を返す。
        /// </summary>
        /// <param name="ct">キャンセルトークン。</param>
        /// <returns>現在の URL 文字列。</returns>
        Task<string> GetUrlAsync(CancellationToken ct = default);

        /// <summary>
        /// WebView2 で現在表示されているページの outerHTML を返す。
        /// </summary>
        /// <param name="ct">キャンセルトークン。</param>
        /// <returns>document.documentElement.outerHTML の文字列。</returns>
        Task<string> GetContentAsync(CancellationToken ct = default);

        /// <summary>
        /// CSS セレクターに一致する最初の要素をクリックする。
        /// </summary>
        /// <param name="selector">CSS セレクター文字列。</param>
        /// <param name="ct">キャンセルトークン。</param>
        /// <exception cref="Exception">要素が見つからない場合。</exception>
        Task ClickAsync(string selector, CancellationToken ct = default);

        /// <summary>
        /// CSS セレクターに一致する最初の入力要素にテキストを入力する。
        /// input/change イベントも発火する。
        /// </summary>
        /// <param name="selector">CSS セレクター文字列。</param>
        /// <param name="text">入力するテキスト。</param>
        /// <param name="ct">キャンセルトークン。</param>
        Task TypeAsync(string selector, string text, CancellationToken ct = default);

        /// <summary>
        /// ページを指定した座標へスクロールする（window.scrollTo）。
        /// </summary>
        /// <param name="x">横方向のピクセル座標。</param>
        /// <param name="y">縦方向のピクセル座標。</param>
        /// <param name="ct">キャンセルトークン。</param>
        Task ScrollAsync(int x, int y, CancellationToken ct = default);

        /// <summary>
        /// OS 標準のフォルダ選択ダイアログを表示し、選択された絶対パスを返す。
        /// </summary>
        /// <param name="ct">キャンセルトークン。</param>
        /// <returns>選択されたフォルダの絶対パス。キャンセルされた場合は空文字列。</returns>
        Task<string> PickFolderAsync(CancellationToken ct = default);
    }
}
