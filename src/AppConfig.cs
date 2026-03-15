using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WebView2AppHost
{
    /// <summary>
    /// app.conf.json の設定値。ファイルがない場合はデフォルト値を使う。
    /// </summary>
    internal sealed class AppConfig
    {
        public string Title      { get; private set; } = "WebView2 App Host";
        public int    Width      { get; private set; } = 1280;
        public int    Height     { get; private set; } = 720;
        public bool   Fullscreen { get; private set; } = false;

        // ---------------------------------------------------------------------------
        // 読み込み
        // ---------------------------------------------------------------------------

        /// <summary>
        /// ZipContentProvider から /app.conf.json を読んで設定を返す。
        /// エントリが存在しない場合はデフォルト値のインスタンスを返す。
        /// </summary>
        public static AppConfig Load(ZipContentProvider zip)
        {
            var cfg = new AppConfig();

            var data = zip.TryGetBytes("/app.conf.json");
            if (data == null) return cfg;

            var json = Encoding.UTF8.GetString(data);

            var title = GetString(json, "title");
            if (!string.IsNullOrEmpty(title)) cfg.Title = title!;

            var w = GetInt(json, "width");
            if (w > 0) cfg.Width = w;

            var h = GetInt(json, "height");
            if (h > 0) cfg.Height = h;

            cfg.Fullscreen = GetBool(json, "fullscreen", cfg.Fullscreen);

            return cfg;
        }

        // ---------------------------------------------------------------------------
        // 最小 JSON パーサー（固定キー専用・外部ライブラリなし）
        // ---------------------------------------------------------------------------

        private static string? GetString(string json, string key)
        {
            var m = Regex.Match(json, $@"""{Regex.Escape(key)}""\s*:\s*""((?:[^""\\]|\\.)*)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int GetInt(string json, string key)
        {
            var m = Regex.Match(json, $@"""{Regex.Escape(key)}""\s*:\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        private static bool GetBool(string json, string key, bool defaultValue)
        {
            var m = Regex.Match(json, $@"""{Regex.Escape(key)}""\s*:\s*(true|false)");
            if (!m.Success) return defaultValue;
            return m.Groups[1].Value == "true";
        }
    }
}
