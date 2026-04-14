using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WebView2AppHost.SystemAgent
{
    /// <summary>
    /// ファイルシステム操作を提供するクラス
    /// </summary>
    public static class FileSystem
    {
        private static string _workspaceRoot = EnsureTrailingSlash(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        private static readonly JavaScriptSerializer s_json = new JavaScriptSerializer();

        private static string EnsureTrailingSlash(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.EndsWith(Path.DirectorySeparatorChar.ToString()) || path.EndsWith(Path.AltDirectorySeparatorChar.ToString())
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// ワークスペースの基準ディレクトリを設定する。
        /// </summary>
        public static string SetWorkspace(string path)
        {
            if (string.IsNullOrEmpty(path)) return _workspaceRoot;
            var target = Path.GetFullPath(path);
            if (!Directory.Exists(target)) return _workspaceRoot;
            
            _workspaceRoot = EnsureTrailingSlash(target);
            return _workspaceRoot;
        }

        /// <summary>
        /// 現在のワークスペースの絶対パスを返す。
        /// </summary>
        public static string GetWorkspace() => _workspaceRoot;

        private static string SecurePath(string path)
        {
            string combined = Path.IsPathRooted(path) ? path : Path.Combine(_workspaceRoot, path);
            var fullPath = Path.GetFullPath(combined);
            
            bool isUnderRoot = fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase) || 
                              fullPath.Equals(_workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

            if (!isUnderRoot)
            {
                throw new UnauthorizedAccessException($"Access denied: Path '{path}' is outside the permitted workspace '{_workspaceRoot}'.");
            }
            return fullPath;
        }

        public static string ListFiles(string dirPath = ".")
        {
            try
            {
                var path = SecurePath(dirPath);
                var entries = Directory.GetFileSystemEntries(path)
                    .Select(p => {
                        var isDir = Directory.Exists(p);
                        return new {
                            name = Path.GetFileName(p),
                            isDirectory = isDir,
                            size = isDir ? 0 : new FileInfo(p).Length,
                            lastModified = File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm:ss")
                        };
                    }).ToArray();
                
                return s_json.Serialize(entries);
            }
            catch (Exception ex)
            {
                return s_json.Serialize(new { error = ex.Message });
            }
        }

        public static string ReadFile(string filePath)
        {
            var path = SecurePath(filePath);
            return File.ReadAllText(path, Encoding.UTF8);
        }

        public static string ReadFileLines(string filePath, int start_line, int end_line)
        {
            try
            {
                var path = SecurePath(filePath);
                if (start_line < 1) start_line = 1;
                if (end_line < start_line) return "";

                var lines = File.ReadLines(path, Encoding.UTF8)
                                .Skip(start_line - 1)
                                .Take(end_line - start_line + 1);
                
                // 行末の改行コードを \n に統一して返す（エージェントが扱いやすくするため）
                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }

        public static string WriteFile(string filePath, string content)
        {
            var path = SecurePath(filePath);
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, Encoding.UTF8);
            return "File written successfully.";
        }

        public static string ReplaceInFile(string filePath, string old_text, string new_text)
        {
            try
            {
                var path = SecurePath(filePath);
                var content = File.ReadAllText(path, Encoding.UTF8);

                // 改行コード (\r\n, \n, \r) の差異を正規表現で吸収する
                // 1. old_text 内の改行を \n に統一し、正規表現としてエスケープ
                var normalizedOld = old_text.Replace("\r\n", "\n").Replace("\r", "\n");
                var escaped = Regex.Escape(normalizedOld);

                // 2. エスケープされた \n (\\n) を、柔軟な改行マッチパターンに置き換える
                var pattern = escaped.Replace("\\n", "(?:\\r\\n|\\n|\\r)");
                
                var regex = new Regex(pattern);
                if (!regex.IsMatch(content))
                {
                    // マッチしなかった場合はヒントを添えてエラーを返す
                    return s_json.Serialize(new { 
                        status = "error", 
                        message = "Old text not found. Check for indentation or whitespace differences. Newlines are handled flexibly, but the rest must match exactly." 
                    });
                }

                // 安全のため、最初に見つかった 1 箇所のみを置換する
                // 置換後の文字列に含まれる '$' が正規表現の置換変数として解釈されないようエスケープする
                var safeNewText = new_text.Replace("$", "$$");
                var newContent = regex.Replace(content, safeNewText, 1);
                File.WriteAllText(path, newContent, Encoding.UTF8);
                return s_json.Serialize(new { status = "success" });
            }
            catch (Exception ex)
            {
                return s_json.Serialize(new { status = "error", message = ex.Message });
            }
        }

        public static string ListDirectoryTree(string dirPath = ".", int max_depth = 2)
        {
            try
            {
                var path = SecurePath(dirPath);
                var sb = new StringBuilder();
                var di = new DirectoryInfo(path);
                WalkTree(di, "", 0, max_depth, sb);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }

        private static void WalkTree(DirectoryInfo dir, string indent, int depth, int maxDepth, StringBuilder sb)
        {
            if (depth > maxDepth) return;

            sb.AppendLine($"{indent} {dir.Name}/");
            var nextIndent = indent + "  ";

            try
            {
                foreach (var d in dir.GetDirectories())
                {
                    WalkTree(d, nextIndent, depth + 1, maxDepth, sb);
                }
                foreach (var f in dir.GetFiles())
                {
                    sb.AppendLine($"{nextIndent} {f.Name} ({f.Length:N0} bytes)");
                }
            }
            catch { /* Skip inaccessible items */ }
        }
    }

    /// <summary>
    /// コマンド実行機能を提供するクラス
    /// </summary>
    public static class Terminal
    {
        private static readonly JavaScriptSerializer s_json = new JavaScriptSerializer();

        public static string Execute(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c chcp 65001 > nul && {command}",
                    WorkingDirectory = FileSystem.GetWorkspace(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) throw new Exception("Failed to start process.");
                    
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();

                    var result = new
                    {
                        stdout = stdout,
                        stderr = stderr,
                        code = proc.ExitCode,
                        ok = proc.ExitCode == 0
                    };
                    return s_json.Serialize(result);
                }
            }
            catch (Exception ex)
            {
                return s_json.Serialize(new { stdout = "", stderr = ex.Message, code = 1, ok = false });
            }
        }
    }
}
