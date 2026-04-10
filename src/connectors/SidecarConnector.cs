using System;
using System.Collections;
using System.Collections.Concurrent;
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
    /// 1 つのサイドカープロセスを管理するコネクター。
    /// </summary>
    public sealed class SidecarConnector : IConnector
    {
        private readonly SidecarEntry    _entry;
        private readonly Encoding        _encoding;
        private readonly CancellationToken _shutdownToken;
        private readonly object _processSync = new object();

        private Action<string>?    _publish;
        private Process?           _process;
        private StreamWriter?      _stdin;
        private readonly SemaphoreSlim _writeLock  = new SemaphoreSlim(1, 1);
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private bool _isReady;
        private bool _disposed;
        private bool _restartScheduled;
        private int _restartCount;

        // 自分が発行したリクエスト ID を保持する（応答を自分に戻すため）
        private readonly ConcurrentDictionary<string, bool> _pendingRequestIds =
            new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        private static readonly JavaScriptSerializer s_json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public SidecarConnector(SidecarEntry entry, CancellationToken shutdownToken = default)
        {
            _entry         = entry ?? throw new ArgumentNullException(nameof(entry));
            _encoding      = ResolveEncoding(entry.Encoding);
            _shutdownToken = shutdownToken;
        }

        public string Name => _entry.Alias;

        public Action<string> Publish
        {
            set => _publish = value;
        }

        public void Deliver(string messageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(messageJson)) return;

            bool isForMe = IsForMe(messageJson);
            bool isResponse = IsResponseForMe(messageJson);

            AppLog.Log(
                AppLog.LogLevel.Debug,
                $"SidecarConnector[{Name}]",
                $"Deliver: isForMe={isForMe}, isResponse={isResponse}, {AppLog.DescribeMessageJson(messageJson)}",
                dataKind: AppLog.LogDataKind.Sensitive);

            if (isForMe)
            {
                if (string.Equals(_entry.Mode, "streaming", StringComparison.OrdinalIgnoreCase))
                    _ = SendToProcessAsync(messageJson);
                else
                    _ = ExecuteCliAsync(messageJson);
                return;
            }

            if (isResponse)
            {
                _ = SendToProcessAsync(messageJson);
            }
        }

        private bool IsForMe(string json)
        {
            try
            {
                var dict = s_json.Deserialize<Dictionary<string, object>>(json);
                if (dict == null) return false;

                if (dict.TryGetValue("jsonrpc", out var jv) && string.Equals(jv?.ToString(), "2.0", StringComparison.OrdinalIgnoreCase))
                {
                    if (dict.TryGetValue("method", out var mv) && mv != null)
                    {
                        var idx = mv.ToString()!.IndexOf('.');
                        if (idx > 0)
                            return string.Equals(mv.ToString()!.Substring(0, idx), Name, StringComparison.OrdinalIgnoreCase);
                    }
                }
                else if (dict.TryGetValue("source", out var sv))
                {
                    return string.Equals(sv?.ToString(), Name, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            }
            catch { return false; }
        }

        private bool IsResponseForMe(string json)
        {
            try
            {
                var dict = s_json.Deserialize<Dictionary<string, object>>(json);
                if (dict == null) return false;

                if (dict.TryGetValue("id", out var idObj) && idObj != null && !dict.ContainsKey("method"))
                {
                    var id = idObj.ToString();
                    return _pendingRequestIds.TryRemove(id, out _);
                }
            }
            catch { }
            return false;
        }

        public void Start()
        {
            if (_disposed) return;
            if (string.Equals(_entry.Mode, "streaming", StringComparison.OrdinalIgnoreCase))
                StartStreamingProcess();
        }

        private void StartStreamingProcess()
        {
            lock (_processSync)
            {
                if (_disposed) return;
                _restartScheduled = false;
                _isReady = false;
                _ready.Reset();
            }

            var psi = BuildProcessStartInfo();
            psi.RedirectStandardInput = true;

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutput;
            _process.ErrorDataReceived  += OnError;
            _process.Exited             += OnExited;

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, _encoding);

            AppLog.Log("INFO", $"SidecarConnector[{Name}]", $"プロセス起動: PID={_process.Id}");

            if (_entry.WaitForReady)
            {
                if (_ready.Wait(10_000, _shutdownToken))
                    AppLog.Log("INFO", $"SidecarConnector[{Name}]", "Ready シグナル受信");
                else
                {
                    _isReady = true;
                    AppLog.Log("WARN", $"SidecarConnector[{Name}]", "Ready タイムアウト。続行します。");
                }
            }
            else { _isReady = true; }
        }

        private async Task SendToProcessAsync(string json)
        {
            if (_stdin == null) return;
            await _writeLock.WaitAsync(_shutdownToken).ConfigureAwait(false);
            try
            {
                await _stdin.WriteLineAsync(json);
                await _stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { AppLog.Log("WARN", $"SidecarConnector[{Name}].Send", ex.Message); }
            finally { _writeLock.Release(); }
        }

        private async Task ExecuteCliAsync(string requestJson)
        {
            try
            {
                var req = s_json.Deserialize<Dictionary<string, object>>(requestJson);
                var id  = req != null && req.TryGetValue("id", out var idV) ? idV : null;

                var psi = BuildProcessStartInfo();
                if (req != null && req.TryGetValue("params", out var pObj))
                {
                    var args = new List<string>(_entry.Args);

                    // 1. 配列形式 (Positional) のパラメータ処理
                    if (pObj is ArrayList pList)
                    {
                        foreach (var p in pList)
                        {
                            var ps = p?.ToString() ?? "";
                            bool replaced = false;
                            for (int i = 0; i < args.Count; i++)
                            {
                                if (args[i] == "{args}") { args[i] = ps; replaced = true; break; }
                            }
                            if (!replaced) args.Add(ps);
                        }
                    }
                    // 2. オブジェクト形式 (Named) のパラメータ処理
                    else if (pObj is IDictionary pDict)
                    {
                        foreach (DictionaryEntry entry in pDict)
                        {
                            var key = entry.Key?.ToString() ?? "";
                            var val = entry.Value?.ToString() ?? "";
                            var placeholder = "{" + key + "}";
                            bool replaced = false;

                            for (int i = 0; i < args.Count; i++)
                            {
                                if (args[i].Contains(placeholder))
                                {
                                    args[i] = args[i].Replace(placeholder, val);
                                    replaced = true;
                                }
                            }

                            // プレースホルダがない場合、自動で --key value 形式で追加する
                            if (!replaced)
                            {
                                args.Add("--" + key);
                                args.Add(val);
                            }
                        }
                    }

                    args.RemoveAll(a => a == "{args}");
                    psi.Arguments = string.Join(" ", args.Select(EscapeArgument));
                }

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                proc.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stderr))
                    AppLog.Log("WARN", $"SidecarConnector[{Name}].CLI.Stderr", $"stderr len={stderr.Length}");

                object result;
                try { result = s_json.DeserializeObject(stdout); }
                catch { result = stdout.Trim(); }

                var response = s_json.Serialize(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0", ["id"] = id ?? (object)0, ["result"] = result, ["source"] = Name,
                });
                _publish?.Invoke(response);
            }
            catch (Exception ex) { AppLog.Log("ERROR", $"SidecarConnector[{Name}].CLI", ex.Message, ex); }
        }

        private string EscapeArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            // スペースや特殊文字が含まれる場合は引用符で囲み、内部の引用符をエスケープ
            bool needsQuotes = arg.Any(c => char.IsWhiteSpace(c) || "&|<>^\"".Contains(c));
            if (!needsQuotes) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            if (!_isReady && _entry.WaitForReady)
            {
                if (e.Data.Contains("\"ready\"") && e.Data.Contains("true"))
                {
                    _isReady = true;
                    _ready.Set();
                    return;
                }
            }

            try
            {
                var msg = s_json.Deserialize<Dictionary<string, object>>(e.Data);
                if (msg != null && msg.TryGetValue("id", out var idObj) && idObj != null && msg.ContainsKey("method"))
                {
                    // メモリリーク対策: 溜まりすぎたら古いものを掃除
                    if (_pendingRequestIds.Count > 1000)
                    {
                        var keys = _pendingRequestIds.Keys.Take(500).ToList();
                        foreach (var k in keys) _pendingRequestIds.TryRemove(k, out _);
                    }
                    _pendingRequestIds.TryAdd(idObj.ToString(), true);
                }
            }
            catch { }

            _publish?.Invoke(e.Data);
        }

        private void OnError(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AppLog.Log("WARN", $"SidecarConnector[{Name}].Stderr", $"stderr len={e.Data.Length}");
        }

        private void OnExited(object sender, EventArgs e)
        {
            Process? exitedProcess;
            int? exitCode = null;
            int restartAttempt;

            lock (_processSync)
            {
                if (_disposed) return;

                exitedProcess = sender as Process;
                if (ReferenceEquals(_process, exitedProcess))
                {
                    try { _stdin?.Dispose(); } catch { }
                    _stdin = null;
                    _process = null;
                }

                try { exitCode = exitedProcess?.ExitCode; } catch { }
                AppLog.Log("WARN", $"SidecarConnector[{Name}]", $"プロセス終了: ExitCode={exitCode}");

                if (_restartScheduled) return;

                if (_restartCount >= 5)
                {
                    AppLog.Log("ERROR", $"SidecarConnector[{Name}]", "再起動上限に達しました");
                    return;
                }

                _restartScheduled = true;
                restartAttempt = _restartCount++;
            }

            try { exitedProcess?.Dispose(); } catch { }

            var delay = TimeSpan.FromSeconds(Math.Pow(2, restartAttempt));
            _ = RestartStreamingProcessAsync(delay);
        }

        private async Task RestartStreamingProcessAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, _shutdownToken).ConfigureAwait(false);
                if (_shutdownToken.IsCancellationRequested) return;
                StartStreamingProcess();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                lock (_processSync) { _restartScheduled = false; }
                AppLog.Log("ERROR", $"SidecarConnector[{Name}].Restart", ex.Message, ex);
            }
        }

        private ProcessStartInfo BuildProcessStartInfo() => new ProcessStartInfo
        {
            FileName               = _entry.Executable,
            Arguments              = string.Join(" ", _entry.Args.Select(EscapeArgument)),
            WorkingDirectory       = _entry.WorkingDirectory,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = _encoding,
            StandardErrorEncoding  = _encoding,
        };

        private static Encoding ResolveEncoding(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return new UTF8Encoding(false);
            try
            {
                var lower = name.ToLowerInvariant();
                if (lower is "utf-8" or "utf8") return new UTF8Encoding(false);
                if (lower is "shift-jis" or "sjis" or "cp932") return Encoding.GetEncoding(932);
                return Encoding.GetEncoding(name);
            }
            catch { return new UTF8Encoding(false); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stdin?.Close(); } catch { }
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(3000);
                }
            }
            catch (Exception ex) { AppLog.Log("WARN", $"SidecarConnector[{Name}].Dispose", ex.Message); }
            finally { _process?.Dispose(); _writeLock.Dispose(); _ready.Dispose(); }
        }
    }
}
