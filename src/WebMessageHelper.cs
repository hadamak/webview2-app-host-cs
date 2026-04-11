using System;

namespace WebView2AppHost
{
    /// <summary>
    /// WebView2 の WebMessageReceivedEventArgs から JSON メッセージを適切に取り出すためのヘルパー。
    /// ユニットテストを容易にするため、EventArgs そのものではなくデリゲートを受け取る。
    /// </summary>
    internal static class WebMessageHelper
    {
        /// <summary>
        /// 文字列としての取得を試みるデリゲート。
        /// </summary>
        public delegate string? TryGetStringDelegate();

        /// <summary>
        /// JSON 文字列としての取得を試みるデリゲート。
        /// </summary>
        public delegate string GetAsJsonDelegate();

        /// <summary>
        /// メッセージを JSON 文字列として取得する。
        /// JS 側が JSON.stringify() で送った場合と、オブジェクトを直接送った場合の両方に対応する。
        /// </summary>
        /// <param name="tryGetString">TryGetWebMessageAsString 相当のデリゲート</param>
        /// <param name="getAsJson">WebMessageAsJson 相当のデリゲート</param>
        /// <returns>パース済みの JSON 文字列</returns>
        public static string GetJsonPayload(TryGetStringDelegate tryGetString, GetAsJsonDelegate getAsJson)
        {
            try
            {
                // JS 側が JSON.stringify({source:"..."}) として送った場合、
                // TryGetWebMessageAsString は中身の JSON 文字列 ("{source:\"...\"}") を返す。
                // WebMessageAsJson はそれをさらにクォートした文字列リテラル ("\"{source:\\\"...\\\"}\"") を返す。
                // 我々が欲しいのは「中身」なので、まず文字列としての取得を試みる。
                string? s = tryGetString();
                if (s != null) return s;
            }
            catch
            {
                // メッセージがオブジェクト型などの場合、TryGetWebMessageAsString は例外を投げる。
                // その場合は WebMessageAsJson を使用する。
            }

            return getAsJson();
        }
    }
}
