using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// app.conf.json の "sidecars" に列挙されたサイドカープロセスと StdIO JSON で通信する汎用プラグイン。
    ///
    /// 動作フロー:
    ///   1. Initialize: AppConfig の sidecars 配列に基づいてサイドカープロセスを起動
    ///   2. JS → C#: HandleWebMessage が { source:"<alias>", ... } を受信
    ///   3. C# → サイドカー: stdin に JSON を書き込む
    ///   4. サイドカー → C#: stdout から JSON を読み、WebView2 へ PostWebMessageAsString
    ///
    /// sidecars フォーマット (app.conf.json):
    ///   "sidecars": [
    ///     {
    ///       "alias": "NodeBackend",
    ///       "mode": "streaming",
    ///       "executable": "node-runtime/node.exe",
    ///       "workingDirectory": "node-runtime",
    ///       "args": ["server.js"],
    ///       "waitForReady": true
    ///     }
    ///   ]
    /// </summary>
    public sealed class GenericSidecarPlugin : IHostPlugin
    {
        // ---------------------------------------------------------------------------
        // フィールド
        // ---------------------------------------------------------------------------

        private readonly WebView2 _webView;
        private readonly Dictionary<string, SidecarProcess> _sidecars =
            new Dictionary<string, SidecarProcess>(StringComparer.OrdinalIgnoreCase);
        
        private bool _disposed;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// GenericSidecarPlugin を生成する。
        /// PluginManager の汎用ローダーから Activator.CreateInstance(type, webView) で呼ばれる。
        /// </summary>
        public GenericSidecarPlugin(WebView2 webView)
        {
            _webView = webView;
        }

        // ---------------------------------------------------------------------------
        // IHostPlugin
        // ---------------------------------------------------------------------------

        public string PluginName => "GenericSidecarPlugin";

        /// <summary>
        /// ホストから app.conf.json の内容を JSON 文字列として受け取り、初期化する。
        /// プラグインは内部で JSON をパースし、"sidecars" 配列だけを抽出する。
        /// ホストの型（AppConfig など）には一切依存しない。
        /// </summary>
        public void Initialize(string configJson)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var conf = serializer.Deserialize<Dictionary<string, object>>(configJson);
                if (conf == null || !conf.TryGetValue("sidecars", out var sidecarsVal))
                {
                    AppLog.Log("INFO", "GenericSidecarPlugin.Initialize", "sidecars が空です");
                    return;
                }

                if (!(sidecarsVal is System.Collections.ArrayList itemList) || itemList.Count == 0)
                {
                    AppLog.Log("INFO", "GenericSidecarPlugin.Initialize", "sidecars が空です");
                    return;
                }

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var item in itemList)
                    TryStartSidecar(baseDir, item);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericSidecarPlugin.Initialize",
                    $"sidecars の読み込みに失敗: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// source フィールドがエイリアスと一致するメッセージを受け取り、サイドカーへ転送する。
        /// </summary>
        public void HandleWebMessage(string webMessageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(webMessageJson)) return;

            try
            {
                var serializer = new JavaScriptSerializer();
                var msg = serializer.Deserialize<Dictionary<string, object>>(webMessageJson);
                if (msg == null || !msg.TryGetValue("source", out var srcObj)) return;

                var source = srcObj?.ToString();
                if (string.IsNullOrEmpty(source)) return;

                if (_sidecars.TryGetValue(source, out var sidecar))
                {
                    _ = sidecar.SendAsync(webMessageJson);
                }
            }
            catch
            {
                // 無視
            }
        }

        // ---------------------------------------------------------------------------
        // サイドカー管理
        // ---------------------------------------------------------------------------

        /// <summary>
        /// SidecarEntry を解析してサイドカープロセスを起動する。
        /// </summary>
        private void TryStartSidecar(SidecarEntry entry)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            TryStartSidecar(baseDir, entry);
        }

        /// <summary>
        /// SidecarEntry を解析してサイドカープロセスを起動する。
        /// </summary>
        private void TryStartSidecar(string baseDir, SidecarEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Alias) || string.IsNullOrEmpty(entry.Executable))
            {
                AppLog.Log("WARN", "GenericSidecarPlugin.TryStartSidecar", "Alias または Executable が空です");
                return;
            }

            // 1. 実行ファイルのフルパスを解決（前回の PATH 検索ロジックを含む）
            string? execPath = ResolveExecutablePath(baseDir, entry.Executable);

            if (execPath == null)
            {
                AppLog.Log("WARN", "GenericSidecarPlugin.TryStartSidecar", $"実行ファイルが見つかりません: {entry.Executable}");
                return;
            }

            // 2. 作業ディレクトリ (CWD) の決定
            string workDir;
            if (string.IsNullOrEmpty(entry.WorkingDirectory))
            {
                workDir = baseDir; 
            }
            else
            {
                // 指定がある場合は アプリ からの相対パスとして解決
                workDir = Path.IsPathRooted(entry.WorkingDirectory)
                    ? entry.WorkingDirectory
                    : Path.Combine(baseDir, entry.WorkingDirectory);
            }

            // 3. ディレクトリの存在確認
            if (!Directory.Exists(workDir))
            {
                AppLog.Log("WARN", "GenericSidecarPlugin", $"作業ディレクトリが見つかりません: {workDir}。baseDir を使用します。");
                workDir = baseDir;
            }

            try
            {
                // 起動（execPath はグローバルな場所、workDir はアプリの場所になる）
                var sidecar = new SidecarProcess(entry.Alias, execPath, workDir, entry.Args, entry.WaitForReady, _webView);
                sidecar.Start();
                _sidecars[entry.Alias] = sidecar;

                AppLog.Log("INFO", "GenericSidecarPlugin", $"サイドカー起動成功: {entry.Alias} (CWD: {workDir})");
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericSidecarPlugin.TryStartSidecar", $"起動失敗: {entry.Alias}", ex);
            }
        }

        /// <summary>
        /// 実行ファイルのパスを、絶対パス、相対パス、環境変数 PATH の順で解決する。
        /// </summary>
        private string? ResolveExecutablePath(string baseDir, string executable)
        {
            // 1. 絶対パスの場合
            if (Path.IsPathRooted(executable))
            {
                return File.Exists(executable) ? executable : null;
            }

            // 2. 相対パスの場合
            var relativePath = Path.Combine(baseDir, executable);
            if (File.Exists(relativePath))
            {
                return Path.GetFullPath(relativePath);
            }

            // 3. 環境変数 PATH から検索
            // node や python のような名前のみ（ディレクトリ区切りを含まない）の場合に検索
            if (!executable.Contains(Path.DirectorySeparatorChar.ToString()) && 
                !executable.Contains(Path.AltDirectorySeparatorChar.ToString()))
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    var paths = pathEnv.Split(Path.PathSeparator);
                    // Windows の実行可能拡張子 (.EXE, .CMD, .BAT 等) を取得
                    var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD")
                        .Split(';')
                        .Select(e => e.Trim().ToUpperInvariant())
                        .ToList();
                    
                    // 拡張子が既に含まれているか確認
                    bool hasExtension = extensions.Any(ext => executable.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

                    foreach (var p in paths)
                    {
                        var combined = Path.Combine(p, executable);
                        
                        // そのままのファイル名で存在するか
                        if (File.Exists(combined)) return Path.GetFullPath(combined);

                        // 拡張子を補完して存在するか (node -> node.exe)
                        if (!hasExtension)
                        {
                            foreach (var ext in extensions)
                            {
                                var withExt = combined + ext;
                                if (File.Exists(withExt)) return Path.GetFullPath(withExt);
                            }
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 辞列からサイドカーを解析して起動する（後方互換用）。
        /// </summary>
        private void TryStartSidecar(string baseDir, object? item)
        {
            if (item is Dictionary<string, object> d)
            {
                // 大文字小文字を区別せずにキーを検索
                var entry = new SidecarEntry
                {
                    Alias = "",
                    Mode = "streaming",
                    Executable = "",
                    WorkingDirectory = "",
                    Args = Array.Empty<string>(),
                    WaitForReady = false
                };

                foreach (var kvp in d)
                {
                    var key = kvp.Key.ToLowerInvariant();
                    var value = kvp.Value;

                    switch (key)
                    {
                        case "alias":
                            entry.Alias = value?.ToString() ?? "";
                            break;
                        case "mode":
                            entry.Mode = value?.ToString() ?? "streaming";
                            break;
                        case "executable":
                            entry.Executable = value?.ToString() ?? "";
                            break;
                        case "workingdirectory":
                            entry.WorkingDirectory = value?.ToString() ?? "";
                            break;
                        case "args":
                            if (value is System.Collections.ArrayList arr)
                                entry.Args = arr.Cast<object>().Select(x => x?.ToString() ?? "").ToArray();
                            break;
                        case "waitforready":
                            if (value is bool b)
                                entry.WaitForReady = b;
                            break;
                    }
                }

                TryStartSidecar(baseDir, entry);
            }
        }

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var sidecar in _sidecars.Values)
            {
                try { sidecar.Dispose(); }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", "GenericSidecarPlugin.Dispose",
                        $"サイドカー [{sidecar.Alias}] の Dispose に失敗: {ex.Message}");
                }
            }
            _sidecars.Clear();
        }

        // ---------------------------------------------------------------------------
        // サイドカープロセス
        // ---------------------------------------------------------------------------

        /// <summary>
        /// サイドカープロセスを管理する内部クラス。
        /// </summary>
        private sealed class SidecarProcess : IDisposable
        {
            public string Alias { get; }
            
            private readonly string _executable;
            private readonly string _workingDirectory;
            private readonly string[] _args;
            private readonly WebView2 _webView;
            private readonly bool _waitForReady;
            
            private Process? _process;
            private StreamWriter? _stdin;
            private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
            private readonly ManualResetEventSlim _readySignal = new ManualResetEventSlim(false);
            private bool _isReady;
            private bool _disposed;

            public SidecarProcess(string alias, string executable, string workingDirectory, string[] args, bool waitForReady, WebView2 webView)
            {
                Alias = alias;
                _executable = executable;
                _workingDirectory = workingDirectory;
                _args = args;
                _waitForReady = waitForReady;
                _webView = webView;
            }

            public void Start()
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _executable,
                    Arguments = string.Join(" ", _args),
                    WorkingDirectory = _workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };

                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += OnOutput;
                _process.ErrorDataReceived += OnError;
                _process.Exited += OnExited;

                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false));

                AppLog.Log("INFO", "SidecarProcess",
                    $"サイドカープロセスを起動しました: alias={Alias}, PID={_process.Id}");

                // waitForReady が true の場合、サイドカーの stdout から Ready シグナルを待機する
                if (_waitForReady)
                {
                    const int timeoutMs = 10000;
                    if (_readySignal.Wait(timeoutMs))
                    {
                        AppLog.Log("INFO", "SidecarProcess",
                            $"サイドカーの Ready シグナルを受信しました: alias={Alias}");
                    }
                    else
                    {
                        _isReady = true; // タイムアウトしても続行
                        AppLog.Log("WARN", "SidecarProcess",
                            $"サイドカーの Ready シグナルがタイムアウトしました ({timeoutMs}ms): alias={Alias}。続行します。");
                    }
                }
                else
                {
                    _isReady = true;
                }
            }

            public async Task SendAsync(string json)
            {
                if (_disposed || _stdin == null) return;
                
                await _writeLock.WaitAsync();
                try
                {
                    await _stdin.WriteLineAsync(json);
                    await _stdin.FlushAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", "SidecarProcess.Send",
                        $"サイドカー [{Alias}] への送信に失敗: {ex.Message}");
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            private void OnOutput(object sender, DataReceivedEventArgs e)
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                // Ready シグナル検出: {"ready":true} を含む行
                if (!_isReady && _waitForReady)
                {
                    if (e.Data.IndexOf("\"ready\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        e.Data.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _isReady = true;
                        _readySignal.Set();
                        return; // Ready シグナルは JS へ転送しない
                    }
                }

                PostToJs(e.Data);
            }

            private void OnError(object sender, DataReceivedEventArgs e)
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                AppLog.Log("WARN", $"SidecarProcess.{Alias}.Stderr", e.Data);
            }

            private void OnExited(object sender, EventArgs e)
            {
                if (_disposed) return;
                
                var code = _process?.ExitCode ?? -1;
                AppLog.Log("WARN", "SidecarProcess",
                    $"サイドカープロセスが終了しました: alias={Alias}, ExitCode={code}");
            }

            private void PostToJs(string json)
            {
                if (_disposed) return;
                if (_webView.IsDisposed || !_webView.IsHandleCreated) return;

                _webView.BeginInvoke(new Action(() =>
                {
                    if (_disposed || _webView.CoreWebView2 == null) return;
                    try
                    {
                        _webView.CoreWebView2.PostWebMessageAsString(json);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log("WARN", "SidecarProcess.PostToJs",
                            $"JS への投稿に失敗: {ex.Message}");
                    }
                }));
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try { _stdin?.Close(); }
                catch { }

                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(3000);
                        AppLog.Log("INFO", "SidecarProcess",
                            $"サイドカープロセスを終了しました: alias={Alias}");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", "SidecarProcess.Dispose",
                        $"サイドカー [{Alias}] のプロセス終了に失敗: {ex.Message}");
                }
                finally
                {
                    _process?.Dispose();
                    _process = null;
                    _writeLock.Dispose();
                    _readySignal.Dispose();
                }
            }
        }
    }
}