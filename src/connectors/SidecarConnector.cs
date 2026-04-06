using System;
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
    ///
    /// <para>
    /// 旧: GenericSidecarPlugin がすべてのサイドカーを管理（1対多）
    /// 新: SidecarConnector 1 インスタンス = 1 プロセス（1対1）
    ///     PluginManager が config を読んで必要な数だけ生成する
    /// </para>
    ///
    /// ルーティング:
    ///   source / method prefix が自分の Name（alias）と一致するメッセージを
    ///   サイドカーの stdin に転送する。
    ///   サイドカーの stdout から受け取ったメッセージは Publish でバスに流す。
    ///
    /// モード:
    ///   streaming … 起動時にプロセスを立ち上げ、常駐させる。
    ///   cli       … メッセージのたびにプロセスを起動し、stdout を受け取って終了させる。
    /// </summary>
    public sealed class SidecarConnector : IConnector
    {
        // -------------------------------------------------------------------
        // フィールド
        // -------------------------------------------------------------------

        private readonly SidecarEntry    _entry;
        private readonly Encoding        _encoding;
        private readonly CancellationToken _shutdownToken;

        private Action<string>?    _publish;
        private Process?           _process;
        private StreamWriter?      _stdin;
        private readonly SemaphoreSlim _writeLock  = new SemaphoreSlim(1, 1);
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private bool _isReady;
        private bool _disposed;

        private static readonly JavaScriptSerializer s_json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // -------------------------------------------------------------------
        // コンストラクタ
        // -------------------------------------------------------------------

        public SidecarConnector(SidecarEntry entry, CancellationToken shutdownToken = default)
        {
            _entry         = entry ?? throw new ArgumentNullException(nameof(entry));
            _encoding      = ResolveEncoding(entry.Encoding);
            _shutdownToken = shutdownToken;
        }

        // -------------------------------------------------------------------
        // IConnector
        // -------------------------------------------------------------------

        public string Name => _entry.Alias;

        public Action<string> Publish
        {
            set => _publish = value;
        }

        public void Deliver(string messageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(messageJson)) return;

            // source / method prefix が自分の alias と一致するか確認
            if (!IsForMe(messageJson)) return;

            if (string.Equals(_entry.Mode, "streaming", StringComparison.OrdinalIgnoreCase))
            {
                _ = SendToProcessAsync(messageJson);
            }
            else // cli
            {
                _ = ExecuteCliAsync(messageJson);
            }
        }

        // -------------------------------------------------------------------
        // 起動
        // -------------------------------------------------------------------

        /// <summary>
        /// streaming モードのプロセスを起動する。
        /// PluginManager.Start() から呼ばれる。
        /// </summary>
        public void Start()
        {
            if (_disposed) return;
            if (string.Equals(_entry.Mode, "streaming", StringComparison.OrdinalIgnoreCase))
                StartStreamingProcess();
        }

        private void StartStreamingProcess()
        {
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

            AppLog.Log("INFO", $"SidecarConnector[{Name}]",
                $"プロセス起動: PID={_process.Id}");

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
            else
            {
                _isReady = true;
            }
        }

        // -------------------------------------------------------------------
        // stdin 送信
        // -------------------------------------------------------------------

        private async Task SendToProcessAsync(string json)
        {
            if (_stdin == null) return;
            await _writeLock.WaitAsync(_shutdownToken).ConfigureAwait(false);
            try
            {
                await _stdin.WriteLineAsync(json).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", $"SidecarConnector[{Name}].Send", ex.Message);
            }
            finally { _writeLock.Release(); }
        }

        // -------------------------------------------------------------------
        // CLI モード
        // -------------------------------------------------------------------

        private async Task ExecuteCliAsync(string requestJson)
        {
            try
            {
                var req = s_json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(requestJson);
                var id  = req != null && req.TryGetValue("id", out var idV) ? idV : null;

                var psi = BuildProcessStartInfo();
                // params を引数に展開
                if (req != null && req.TryGetValue("params", out var pObj))
                {
                    var args = new System.Collections.Generic.List<string>(_entry.Args);
                    if (pObj is System.Collections.ArrayList pList)
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
                    args.RemoveAll(a => a == "{args}");
                    psi.Arguments = string.Join(" ",
                        args.Select(a => a.Contains(" ") ? $"\"{a}\"" : a));
                }

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                proc.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stderr))
                    AppLog.Log("WARN", $"SidecarConnector[{Name}].CLI.Stderr", stderr);

                object result;
                try { result = s_json.DeserializeObject(stdout); }
                catch { result = stdout.Trim(); }

                var response = s_json.Serialize(new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"]      = id ?? (object)0,
                    ["result"]  = result,
                    ["source"]  = Name,
                });
                _publish?.Invoke(response);
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", $"SidecarConnector[{Name}].CLI", ex.Message, ex);
            }
        }

        // -------------------------------------------------------------------
        // stdout 受信
        // -------------------------------------------------------------------

        private void OnOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            // Ready シグナル検出
            if (!_isReady && _entry.WaitForReady)
            {
                if (e.Data.Contains("\"ready\"") && e.Data.Contains("true"))
                {
                    _isReady = true;
                    _ready.Set();
                    return;
                }
            }

            // バスに流す
            _publish?.Invoke(e.Data);
        }

        private void OnError(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                AppLog.Log("WARN", $"SidecarConnector[{Name}].Stderr", e.Data);
        }

        private void OnExited(object sender, EventArgs e)
        {
            if (!_disposed)
                AppLog.Log("WARN", $"SidecarConnector[{Name}]",
                    $"プロセス終了: ExitCode={_process?.ExitCode}");
        }

        // -------------------------------------------------------------------
        // ヘルパー
        // -------------------------------------------------------------------

        private bool IsForMe(string json)
        {
            try
            {
                var dict = s_json.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
                if (dict == null) return false;

                if (dict.TryGetValue("jsonrpc", out var jv)
                    && string.Equals(jv?.ToString(), "2.0", StringComparison.OrdinalIgnoreCase))
                {
                    if (dict.TryGetValue("method", out var mv) && mv != null)
                    {
                        var idx = mv.ToString()!.IndexOf('.');
                        if (idx > 0)
                            return string.Equals(mv.ToString()!.Substring(0, idx),
                                Name, StringComparison.OrdinalIgnoreCase);
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

        private ProcessStartInfo BuildProcessStartInfo() => new ProcessStartInfo
        {
            FileName               = _entry.Executable,
            Arguments              = string.Join(" ",
                _entry.Args.Select(a => a.Contains(" ") ? $"\"{a}\"" : a)),
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

        // -------------------------------------------------------------------
        // IDisposable
        // -------------------------------------------------------------------

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
                    AppLog.Log("INFO", $"SidecarConnector[{Name}]", "プロセス終了");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", $"SidecarConnector[{Name}].Dispose", ex.Message);
            }
            finally
            {
                _process?.Dispose();
                _writeLock.Dispose();
                _ready.Dispose();
            }
        }
    }
}
