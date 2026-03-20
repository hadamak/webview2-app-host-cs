using System;
using System.Diagnostics;
using System.IO;

namespace WebView2AppHost
{
    /// <summary>
    /// アプリケーション全体の軽量ログユーティリティ。
    /// Debug.WriteLine と %LOCALAPPDATA%\&lt;EXE名&gt;\app.log へのデュアル出力を行う。
    /// テスト時は Override プロパティで出力先を差し替え可能。
    /// </summary>
    internal static class AppLog
    {
        private static readonly object _lock = new object();

        /// <summary>
        /// volatile により GetLogPath() のスレッドセーフな二重初期化を防ぐ。
        /// </summary>
        private static volatile string? _logPath;

        /// <summary>
        /// ログファイルのローテーションしきい値（10 MB）。
        /// このサイズを超えたら起動時に古いログをリネームして新規作成する。
        /// </summary>
        private const long RotateThresholdBytes = 10 * 1024 * 1024;

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
            // タイムスタンプは一度だけ付与する。WriteToFile にはこの文字列をそのまま渡す。
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
                        // content はすでにタイムスタンプ付きのため、そのまま書き出す。
                        Override.WriteLine(content);
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

                    RotateIfNeeded(path);

                    File.AppendAllText(path, content + Environment.NewLine);
                }
            }
            catch
            {
                // ロギング自体の失敗は握りつぶす（本体の動作を壊さない）
            }
        }

        /// <summary>
        /// ログファイルが RotateThresholdBytes を超えていれば
        /// .bak にリネームして新規ファイルから書き始める。
        /// 呼び出し元は _lock を保持していること。
        /// </summary>
        private static void RotateIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < RotateThresholdBytes) return;

                var bakPath = Path.ChangeExtension(path, ".bak");
                // File.Move の overwrite オーバーロードは .NET 5 以降のため、
                // .NET 4.7.2 では既存の .bak を先に削除してから移動する。
                if (File.Exists(bakPath)) File.Delete(bakPath);
                File.Move(path, bakPath);
            }
            catch
            {
                // ローテーション失敗は無視して追記を継続する
            }
        }

        private static string? GetLogPath()
        {
            // volatile フィールドへの二重チェックで初期化コストを最小化する。
            if (_logPath != null) return _logPath;

            try
            {
                var exeName = Path.GetFileNameWithoutExtension(
                    Process.GetCurrentProcess().MainModule?.FileName ?? "WebView2AppHost");
                var localAppData = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                _logPath = Path.Combine(localAppData, exeName, "app.log");
                return _logPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
