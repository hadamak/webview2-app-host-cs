using System;
using System.Text.RegularExpressions;

namespace WebView2AppHost
{
    /// <summary>
    /// カスタムスキーム (https://app.local/) のリソース応答に必要な
    /// 純粋ロジックを提供するユーティリティ。
    /// </summary>
    internal static class WebResourceHandler
    {
        // ⑤ 修正: 毎回パースされていた Regex を static readonly に昇格し Compiled を付与する。
        // Range ヘッダはリクエストごとに呼ばれるため、コンパイル済み Regex の効果が大きい。
        private static readonly Regex s_rangeRegex =
            new Regex(@"^bytes=(\d*)-(\d*)$", RegexOptions.Compiled);

        /// <summary>
        /// Range ヘッダを解析して (start, end) を返す。
        /// フォーマット不正・逆転レンジは null（416 を返すべきケース）。
        /// end が total を超える場合は total-1 にクランプする（動画シーク互換）。
        /// </summary>
        public static (long start, long end)? ParseRange(string header, long total)
        {
            if (total <= 0) return null;

            var m = s_rangeRegex.Match(header);
            if (!m.Success) return null;

            var startStr = m.Groups[1].Value;
            var endStr   = m.Groups[2].Value;

            long start, end;

            if (string.IsNullOrEmpty(startStr))
            {
                // suffix range: "bytes=-500" → 末尾 500 バイト
                if (!long.TryParse(endStr, out var suffix) || suffix <= 0) return null;
                start = total - suffix;
                end   = total - 1;
            }
            else
            {
                if (!long.TryParse(startStr, out start) || start < 0) return null;

                if (string.IsNullOrEmpty(endStr))
                {
                    end = total - 1;
                }
                else
                {
                    if (!long.TryParse(endStr, out end) || end < 0) return null;
                }
            }

            // end のクランプは維持（ブラウザが total を超えた end を送ることがある）
            end = Math.Min(end, total - 1);
            if (start >= total || start > end) return null;

            return (Math.Max(0, start), end);
        }

        /// <summary>
        /// 通常レスポンス (200 OK) 用のヘッダ文字列を構築する。
        /// </summary>
        public static string BuildFullResponseHeaders(string mime, long contentLength)
        {
            return
                $"Content-Type: {mime}\r\n" +
                $"Content-Length: {contentLength}\r\n" +
                "Accept-Ranges: bytes\r\n" +
                "Cache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: *";
        }

        /// <summary>
        /// 部分レスポンス (206 Partial Content) 用のヘッダ文字列を構築する。
        /// </summary>
        public static string BuildPartialResponseHeaders(string mime, long start, long end, long total)
        {
            var length = end - start + 1;
            return
                $"Content-Type: {mime}\r\n" +
                $"Content-Range: bytes {start}-{end}/{total}\r\n" +
                $"Content-Length: {length}\r\n" +
                "Accept-Ranges: bytes\r\n" +
                "Cache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: *";
        }

        /// <summary>
        /// 416 Range Not Satisfiable 用のヘッダ文字列を構築する。
        /// </summary>
        public static string BuildRangeNotSatisfiableHeaders(long total)
        {
            return
                $"Content-Range: bytes */{total}\r\n" +
                "Access-Control-Allow-Origin: *";
        }
    }
}
