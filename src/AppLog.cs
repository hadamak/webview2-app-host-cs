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

        public static void Log(string level, string source, string message, Exception? ex = null)
        {
            var line = ex == null
                ? $"[{level}] [{source}] {message}"
                : $"[{level}] [{source}] {message}: {ex.GetType().Name}: {ex.Message}";

            Write(line);

            if (ex?.StackTrace != null)
                Write(ex.StackTrace);
        }

        private static void Write(string line)
        {
            var timestamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
            System.Diagnostics.Debug.WriteLine(timestamped);
            WriteToFile(timestamped);
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
