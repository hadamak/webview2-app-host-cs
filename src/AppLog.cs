using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace WebView2AppHost
{
    /// <summary>
    /// アプリケーション全体の軽量ログユーティリティ。
    /// Debug.WriteLine と %LOCALAPPDATA%\&lt;EXE名&gt;\app.log へのデュアル出力を行う。
    /// テスト時は Override プロパティで出力先を差し替え可能。
    /// </summary>
    internal static class AppLog
    {
        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warn = 2,
            Error = 3,
        }

        public enum LogDataKind
        {
            General,
            Sensitive,
        }

        private static readonly object _lock = new object();

        /// <summary>
        /// _logPath 初期化の排他制御用。_lock とは別に用意することで
        /// Write 中の初期化呼び出しによるデッドロードを防ぐ。
        /// </summary>
        private static readonly object _initLock = new object();

        /// <summary>
        /// volatile により GetLogPath() のスレッドセーフな二重初期化を防ぐ。
        /// </summary>
        private static volatile string? _logPath;
        private static StreamWriter? _writer;

        /// <summary>
        /// ログファイルのローテーションしきい値（10 MB）。
        /// このサイズを超えたら起動時に古いログをリネームして新規作成する。
        /// </summary>
        private const long RotateThresholdBytes = 10 * 1024 * 1024;

        /// <summary>
        /// テスト用: null 以外が設定されていればファイル出力の代わりにこちらへ書き出す。
        /// </summary>
        internal static TextWriter? Override { get; set; }

        private static readonly JavaScriptSerializer s_json = new JavaScriptSerializer();

        internal static bool EnableFileOutput
        {
            get
            {
#if SECURE_OFFLINE
                return false;
#else
                return true;
#endif
            }
        }

        internal static LogLevel MinimumLevel
        {
            get
            {
#if DEBUG
                return LogLevel.Debug;
#elif SECURE_OFFLINE
                return LogLevel.Warn;
#else
                return LogLevel.Info;
#endif
            }
        }

        [Obsolete("Use Log(LogLevel, ...) instead")]
        public static void Log(string level, string source, string message, Exception? ex = null)
            => Log(ParseLevel(level), source, message, ex, LogDataKind.General);

        public static void Log(LogLevel level, string source, string message, Exception? ex = null, LogDataKind dataKind = LogDataKind.General)
        {
            if (!ShouldWrite(level, dataKind)) return;

            var line = $"[{level}] [{source}] {message}";
            if (ex != null)
            {
                line += $": {ex.GetType().Name}: {ex.Message}";
            }
            Write(line);

            if (ex != null)
            {
                LogExceptionDetails(ex);
            }
        }

        internal static bool ShouldWrite(LogLevel level, LogDataKind dataKind = LogDataKind.General)
        {
            if (level < MinimumLevel) return false;
            if (dataKind == LogDataKind.Sensitive && !IsSensitiveLoggingEnabled()) return false;
            return true;
        }

        internal static string DescribeMessageJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "empty";
            var safeJson = json!;

            try
            {
                var dict = s_json.Deserialize<Dictionary<string, object>>(safeJson);
                if (dict == null) return $"text(len={safeJson.Length})";

                var parts = new List<string>();

                if (dict.TryGetValue("source", out var source) && source != null)
                    parts.Add($"source={source}");

                if (dict.TryGetValue("method", out var method) && method != null)
                    parts.Add($"method={method}");

                if (dict.TryGetValue("id", out var id) && id != null)
                    parts.Add($"id={id}");

                if (dict.TryGetValue("messageId", out var messageId) && messageId != null)
                    parts.Add($"messageId={messageId}");

                if (dict.ContainsKey("result"))
                    parts.Add($"result={DescribeValue(dict["result"])}");

                if (dict.TryGetValue("error", out var error) && error != null)
                    parts.Add($"error={DescribeError(error)}");

                if (dict.TryGetValue("params", out var parameters))
                    parts.Add($"params={DescribeValue(parameters)}");

                return parts.Count == 0 ? $"json(keys={dict.Count})" : string.Join(", ", parts);
            }
            catch
            {
                return $"text(len={safeJson.Length})";
            }
        }

        internal static string DescribeResultSummary(object? value)
            => DescribeValue(value);

        internal static string DescribePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "(empty)";
            var safePath = path!;
            return Path.GetFileName(safePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        internal static string DescribeUri(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "(empty)";
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri == null) return "invalid-uri";

            var target = uri.GetLeftPart(UriPartial.Path);
            if (!string.IsNullOrEmpty(uri.Query)) target += "?...";
            return target;
        }

        private static void LogExceptionDetails(Exception ex)
        {
            if (ex.StackTrace != null)
            {
                Write(ex.StackTrace);
            }

            if (ex.InnerException != null)
            {
                Write($"---> (InnerException) {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                LogExceptionDetails(ex.InnerException);
            }
        }

        private static void Write(string line)
        {
            // タイムスタンプは一度だけ付与する。WriteToFile にはこの文字列をそのまま渡す。
            var timestamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
            System.Diagnostics.Debug.WriteLine(timestamped);
            WriteToFile(timestamped);
        }

        // After
        private static void WriteToFile(string content)
        {
            try
            {
                if (Override != null)
                {
                    lock (_lock)
                    {
                        Override.WriteLine(content);
                        Override.Flush();
                    }
                    return;
                }

                if (!EnableFileOutput) return;

                var path = GetLogPath();
                if (path == null) return;

                lock (_lock)
                {
                    var dir = Path.GetDirectoryName(path);
                    if (dir != null && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // ローテーションが必要な場合は Writer を閉じてから実行する
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length >= RotateThresholdBytes)
                        {
                            _writer?.Flush();
                            _writer?.Dispose();
                            _writer = null;
                            RotateIfNeeded(path);
                        }
                    }
                    catch { /* ローテーション失敗は無視 */ }

                    if (_writer == null)
                        _writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8) { AutoFlush = false };

                    _writer.WriteLine(content);
                    _writer.Flush();
                }
            }
            catch
            {
                // Writer が壊れた場合は破棄して次回再生成させる
                try { lock (_lock) { _writer?.Dispose(); _writer = null; } } catch { }
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
            // volatile フィールドへの double-check locking で初期化を1回に限定する。
            if (_logPath != null) return _logPath;

            lock (_initLock)
            {
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

        private static LogLevel ParseLevel(string level)
        {
            switch ((level ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "DEBUG":
                case "TRACE":
                case "VERBOSE":
                    return LogLevel.Debug;
                case "INFO":
                case "INFORMATION":
                    return LogLevel.Info;
                case "WARN":
                case "WARNING":
                    return LogLevel.Warn;
                case "ERROR":
                case "FATAL":
                    return LogLevel.Error;
                default:
                    return LogLevel.Info;
            }
        }

        private static bool IsSensitiveLoggingEnabled()
        {
#if DEBUG && !SECURE_OFFLINE
            return true;
#else
            return false;
#endif
        }

        private static string DescribeValue(object? value)
        {
            if (value == null) return "null";
            if (value is string s) return $"string(len={s.Length})";
            if (value is IDictionary dict) return $"object(keys={dict.Count})";
            if (value is ICollection collection) return $"list(count={collection.Count})";
            if (value is Array array) return $"array(len={array.Length})";

            var type = value.GetType();
            if (type.IsPrimitive || value is decimal)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? type.Name;

            return type.Name;
        }

        private static string DescribeError(object error)
        {
            if (error is Dictionary<string, object> errorDict)
            {
                var code = errorDict.TryGetValue("code", out var codeObj) ? codeObj?.ToString() : "?";
                var message = errorDict.TryGetValue("message", out var messageObj) ? messageObj?.ToString() : "?";
                return $"code={code}, message={message}";
            }

            return DescribeValue(error);
        }
    }
}
