using System;
using System.Diagnostics;
using System.IO;

namespace WebView2AppHost
{
    /// <summary>
    /// アプリケーション全体の軽量ログユーティリティ。
    /// Debug.WriteLine と %LOCALAPPDATA%\&lt;EXE名&gt;\error.log へのデュアル出力を行う。
    /// テスト時は Override プロパティで出力先を差し替え可能。
    /// </summary>
    internal static class AppLog
    {
        private static readonly object _lock = new object();
        private static string? _logPath;

        /// <summary>
        /// テスト用: null 以外が設定されていればファイル出力の代わりにこちらへ書き出す。
        /// </summary>
        internal static TextWriter? Override { get; set; }

        /// <summary>
        /// エラーレベルのログを出力する。
        /// </summary>
        public static void Error(string source, Exception ex)
        {
            var message = $"[ERROR] [{source}] {ex.GetType().Name}: {ex.Message}";
            var detail  = $"{message}\n{ex.StackTrace}";

            Debug.WriteLine(detail);
            WriteToFile(detail);
        }

        /// <summary>
        /// 警告レベルのログを出力する。
        /// </summary>
        public static void Warn(string source, string message)
        {
            var line = $"[WARN] [{source}] {message}";

            Debug.WriteLine(line);
            WriteToFile(line);
        }

        /// <summary>
        /// 警告レベルのログを例外付きで出力する。
        /// </summary>
        public static void Warn(string source, string message, Exception ex)
        {
            var line = $"[WARN] [{source}] {message}: {ex.GetType().Name}: {ex.Message}";

            Debug.WriteLine(line);
            WriteToFile(line);
        }

        private static void WriteToFile(string content)
        {
            try
            {
                if (Override != null)
                {
                    lock (_lock)
                    {
                        Override.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}");
                        Override.Flush();
                    }
                    return;
                }

                var path = GetLogPath();
                if (path == null) return;

                lock (_lock)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (dir != null && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.AppendAllText(path,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}{Environment.NewLine}");
                }
            }
            catch
            {
                // ロギング自体の失敗は握りつぶす（本体の動作を壊さない）
            }
        }

        private static string? GetLogPath()
        {
            if (_logPath != null) return _logPath;

            try
            {
                var exeName = Path.GetFileNameWithoutExtension(
                    Process.GetCurrentProcess().MainModule?.FileName ?? "WebView2AppHost");
                var localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                _logPath = Path.Combine(localAppData, exeName, "error.log");
                return _logPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
