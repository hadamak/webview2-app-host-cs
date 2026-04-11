using System.Collections.Generic;
using System.IO;

namespace WebView2AppHost
{
    /// <summary>
    /// ファイル拡張子から MIME タイプを判定するユーティリティ。
    /// </summary>
    internal static class MimeTypes
    {
        private static readonly Dictionary<string, string> s_map =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            // ドキュメント
            { ".html", "text/html; charset=utf-8" },
            { ".htm",  "text/html; charset=utf-8" },
            { ".css",  "text/css; charset=utf-8"  },
            { ".js",   "text/javascript"           },
            { ".mjs",  "text/javascript"           },
            { ".json", "application/json"          },
            { ".webmanifest", "application/manifest+json" },
            { ".xml",  "application/xml"           },
            { ".txt",  "text/plain; charset=utf-8" },
            { ".md",   "text/markdown"             },

            // アプリケーション
            { ".wasm", "application/wasm"          },
            { ".pdf",  "application/pdf"           },

            // 静止画
            { ".png",  "image/png"                 },
            { ".jpg",  "image/jpeg"                },
            { ".jpeg", "image/jpeg"                },
            { ".gif",  "image/gif"                 },
            { ".svg",  "image/svg+xml"             },
            { ".svgz", "image/svg+xml"             },
            { ".webp", "image/webp"                },
            { ".avif", "image/avif"                },
            { ".jxl",  "image/jxl"                },
            { ".heic", "image/heic"                },
            { ".heif", "image/heif"                },
            { ".ico",  "image/x-icon"              },
            { ".bmp",  "image/bmp"                 },
            { ".tiff", "image/tiff"                },
            { ".tif",  "image/tiff"                },

            // 音声
            { ".mp3",  "audio/mpeg"                },
            { ".ogg",  "audio/ogg"                 },
            { ".opus", "audio/ogg; codecs=opus"    },
            { ".wav",  "audio/wav"                 },
            { ".flac", "audio/flac"                },
            { ".aac",  "audio/aac"                 },
            { ".m4a",  "audio/mp4"                 },

            // 動画
            { ".mp4",  "video/mp4"                 },
            { ".webm", "video/webm"                },
            { ".ogv",  "video/ogg"                 },
            { ".mov",  "video/quicktime"           },
            { ".avi",  "video/x-msvideo"           },

            // フォント
            { ".ttf",  "font/ttf"                  },
            { ".otf",  "font/otf"                  },
            { ".woff", "font/woff"                 },
            { ".woff2","font/woff2"                },

            // データ
            { ".csv",  "text/csv"                  },
            { ".tsv",  "text/tab-separated-values" },

            // アーカイブ
            { ".zip",  "application/zip"           },
        };

        /// <summary>
        /// 指定されたパスの拡張子に基づいて MIME タイプを取得する。
        /// 未知の拡張子の場合は application/octet-stream を返す。
        /// </summary>
        /// <param name="path">ファイルパスまたはファイル名</param>
        /// <returns>MIME タイプ文字列</returns>
        public static string FromPath(string path)
        {
            var ext = Path.GetExtension(path);
            return s_map.TryGetValue(ext, out var mime)
                ? mime
                : "application/octet-stream";
        }
    }
}
