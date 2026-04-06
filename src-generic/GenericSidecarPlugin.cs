using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

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

        private readonly PluginContext _ctx;
        private readonly Dictionary<string, SidecarProcess> _sidecars =
            new Dictionary<string, SidecarProcess>(StringComparer.OrdinalIgnoreCase);
        
        private readonly Dictionary<string, SidecarEntry> _sidecarEntries =
            new Dictionary<string, SidecarEntry>(StringComparer.OrdinalIgnoreCase);
        
        private bool _disposed;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// GenericSidecarPlugin を生成する。
        /// PluginManager の汎用ローダーから Activator.CreateInstance(type, webView) で呼ばれる。
        /// </summary>
        public GenericSidecarPlugin(PluginContext ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        }

        // ---------------------------------------------------------------------------
        // IHostPlugin
        // ---------------------------------------------------------------------------

        public string PluginName => "GenericSidecarPlugin";

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
                {
                    var entry = ParseSidecarEntry(item);
                    if (entry == null) continue;

                    // 1. 設定を保持
                    _sidecarEntries[entry.Alias] = entry;

                    // 2. 実行ファイルのフルパスを解決
                    string? execPath = ResolveExecutablePath(baseDir, entry.Executable);
                    if (execPath == null)
                    {
                        AppLog.Log("WARN", "GenericSidecarPlugin", $"実行ファイルが見つかりません: {entry.Executable} (alias={entry.Alias})");
                        continue;
                    }
                    entry.Executable = execPath;

                    // 3. 作業ディレクトリの解決
                    if (string.IsNullOrEmpty(entry.WorkingDirectory))
                    {
                        entry.WorkingDirectory = baseDir;
                    }
                    else
                    {
                        entry.WorkingDirectory = Path.IsPathRooted(entry.WorkingDirectory)
                            ? entry.WorkingDirectory
                            : Path.Combine(baseDir, entry.WorkingDirectory);
                    }

                    // 4. streaming モードのみ即時起動
                    if (string.Equals(entry.Mode, "streaming", StringComparison.OrdinalIgnoreCase))
                    {
                        TryStartSidecar(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericSidecarPlugin.Initialize",
                    $"sidecars の読み込みに失敗: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 汎用的な Dictionary から SidecarEntry を生成する。
        /// </summary>
        private SidecarEntry? ParseSidecarEntry(object? item)
        {
            if (!(item is Dictionary<string, object> d)) return null;

            var entry = new SidecarEntry { Mode = "streaming" };
            foreach (var kvp in d)
            {
                var key = kvp.Key.ToLowerInvariant();
                var val = kvp.Value;
                switch (key)
                {
                    case "alias": entry.Alias = val?.ToString() ?? ""; break;
                    case "mode": entry.Mode = val?.ToString() ?? "streaming"; break;
                    case "executable": entry.Executable = val?.ToString() ?? ""; break;
                    case "workingdirectory": entry.WorkingDirectory = val?.ToString() ?? ""; break;
                    case "args":
                        if (val is System.Collections.ArrayList arr)
                            entry.Args = arr.Cast<object>().Select(x => x?.ToString() ?? "").ToArray();
                        break;
                    case "encoding": entry.Encoding = val?.ToString() ?? "utf-8"; break;
                    case "waitforready": if (val is bool b) entry.WaitForReady = b; break;
                }
            }
            return entry;
        }

        private Encoding GetEncoding(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return new UTF8Encoding(false);
            
            var lower = name.ToLowerInvariant();
            try
            {
                if (lower == "utf-8" || lower == "utf8") return new UTF8Encoding(false);
                if (lower == "shift-jis" || lower == "sjis" || lower == "cp932") return Encoding.GetEncoding(932);
                if (lower == "oem" || lower == "default") return Console.OutputEncoding;
                return Encoding.GetEncoding(name);
            }
            catch
            {
                return new UTF8Encoding(false);
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
                if (msg == null) return;

                string? source = null;
                object? requestId = null;

                // JSON-RPC 2.0 形式の検出
                if (msg.TryGetValue("jsonrpc", out var jsonrpcObj) &&
                    string.Equals(jsonrpcObj?.ToString(), "2.0", StringComparison.OrdinalIgnoreCase))
                {
                    msg.TryGetValue("id", out requestId);
                    if (msg.TryGetValue("method", out var methodObj) && methodObj != null)
                    {
                        var methodStr = methodObj.ToString();
                        if (!string.IsNullOrEmpty(methodStr))
                        {
                            var dotIdx = methodStr.IndexOf('.');
                            if (dotIdx > 0) source = methodStr.Substring(0, dotIdx);
                        }
                    }
                }
                else
                {
                    if (msg.TryGetValue("source", out var srcObj)) source = srcObj?.ToString();
                }

                if (string.IsNullOrEmpty(source)) return;

                // 1. 常駐サイドカー (streaming) をチェック
                if (_sidecars.TryGetValue(source!, out var sidecar))
                {
                    _ = sidecar.SendAsync(webMessageJson);
                    return;
                }

                // 2. オンデマンドサイドカー (cli) をチェック
                if (_sidecarEntries.TryGetValue(source!, out var entry) &&
                    string.Equals(entry.Mode, "cli", StringComparison.OrdinalIgnoreCase))
                {
                    _ = ExecuteCliAsync(entry, msg, webMessageJson);
                }
            }
            catch { }
        }

        /// <summary>
        /// cli モードのサイドカーを起動し、結果を JS に返す。
        /// </summary>
        private async Task ExecuteCliAsync(SidecarEntry entry, Dictionary<string, object> msg, string originalJson)
        {
            try
            {
                var requestId = msg.ContainsKey("id") ? msg["id"] : null;
                var args = new List<string>(entry.Args);

                // JS からの params を引数に展開する
                if (msg.TryGetValue("params", out var pObj))
                {
                    if (pObj is System.Collections.ArrayList pList)
                    {
                        foreach (var p in pList)
                        {
                            var pStr = p?.ToString() ?? "";
                            // {args} プレースホルダがあれば置換、なければ末尾に追加
                            bool replaced = false;
                            for (int i = 0; i < args.Count; i++)
                            {
                                if (args[i] == "{args}")
                                {
                                    args[i] = pStr;
                                    replaced = true;
                                    break;
                                }
                            }
                            if (!replaced) args.Add(pStr);
                        }
                    }
                    else if (pObj is Dictionary<string, object> pDict)
                    {
                        // key-value は JSON 文字列として渡すか、あるいは無視するか
                        // ここでは簡易的に JSON 文字列化して渡す
                        args.Add(new JavaScriptSerializer().Serialize(pDict));
                    }
                }

                // {args} が残っている場合は削除
                args.RemoveAll(a => a == "{args}");

                var psi = new ProcessStartInfo
                {
                    FileName = entry.Executable,
                    Arguments = string.Join(" ", args.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)),
                    WorkingDirectory = entry.WorkingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = GetEncoding(entry.Encoding),
                    StandardErrorEncoding = GetEncoding(entry.Encoding)
                };

                AppLog.Log("INFO", "GenericSidecarPlugin.CLI", $"Execute: {psi.FileName} {psi.Arguments}");

                using (var process = new Process { StartInfo = psi })
                {
                    process.Start();
                    
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    await Task.WhenAll(stdoutTask, stderrTask);
                    process.WaitForExit();

                    var stdout = await stdoutTask;
                    var stderr = await stderrTask;

                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        AppLog.Log("WARN", $"Sidecar.{entry.Alias}.Stderr", stderr);
                    }

                    // 結果を JS に送信
                    // stdout が有効な JSON であればパースして result に入れる。そうでなければ文字列。
                    object result;
                    try { result = new JavaScriptSerializer().DeserializeObject(stdout); }
                    catch { result = stdout.Trim(); }

                    SendResponseToJs(requestId, result, entry.Alias);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "GenericSidecarPlugin.ExecuteCli", ex.Message, ex);
            }
        }

        private void SendResponseToJs(object? id, object result, string alias)
        {
            var response = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? 0,
                ["result"] = result,
                ["source"] = alias
            };

            var json = new JavaScriptSerializer().Serialize(response);
            _ctx.PostMessage(json);
        }

        // ---------------------------------------------------------------------------
        // サイドカー管理
        // ---------------------------------------------------------------------------

        /// <summary>
        /// SidecarEntry を解析してサイドカープロセスを起動する。
        /// </summary>
        private void TryStartSidecar(SidecarEntry entry)
        {
            try
            {
                // すでに解決済みの Executable と WorkingDirectory を使用
                var sidecar = new SidecarProcess(entry, _ctx, GetEncoding);
                sidecar.Start();
                _sidecars[entry.Alias] = sidecar;

                AppLog.Log("INFO", "GenericSidecarPlugin", $"サイドカー(streaming)起動成功: {entry.Alias}");
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
            // Initialize 側で ParseSidecarEntry を呼ぶようになったため、このメソッドは不要になったか
            // あるいは互換性のために残す。現在は Initialize 内で処理している。
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
            private readonly string _encoding;
            private readonly PluginContext _ctx;
            private readonly bool _waitForReady;
            private readonly Func<string, Encoding> _getEncoding;
            
            private Process? _process;
            private StreamWriter? _stdin;
            private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
            private readonly ManualResetEventSlim _readySignal = new ManualResetEventSlim(false);
            private bool _isReady;
            private bool _disposed;

            public SidecarProcess(SidecarEntry entry, PluginContext ctx, Func<string, Encoding> getEncoding)
            {
                Alias = entry.Alias;
                _executable = entry.Executable;
                _workingDirectory = entry.WorkingDirectory;
                _args = entry.Args;
                _encoding = entry.Encoding;
                _ctx = ctx;
                _waitForReady = entry.WaitForReady;
                _getEncoding = getEncoding;
            }

            public void Start()
            {
                var encoding = _getEncoding(_encoding);
                var psi = new ProcessStartInfo
                {
                    FileName = _executable,
                    Arguments = string.Join(" ", _args.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)),
                    WorkingDirectory = _workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = encoding,
                    StandardErrorEncoding = encoding,
                };

                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += OnOutput;
                _process.ErrorDataReceived += OnError;
                _process.Exited += OnExited;

                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                _stdin = new StreamWriter(_process.StandardInput.BaseStream, encoding);

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
                try
                {
                    _ctx.PostMessage(json);
                }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", "SidecarProcess.PostToJs",
                        $"メッセージ送信に失敗: {ex.Message}");
                }
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
