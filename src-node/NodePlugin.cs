using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2AppHost
{
    /// <summary>
    /// Node.js サイドカープロセスと StdIO JSON で通信する IHostPlugin 実装。
    ///
    /// 動作フロー:
    ///   1. Initialize: EXE 隣接の node-runtime/node.exe を子プロセスとして起動
    ///   2. JS → C#: HandleWebMessage が { source:"Node", ... } を受信
    ///   3. C# → Node.js: stdin に JSON を書き込む
    ///   4. Node.js → C#: stdout から JSON を読み、WebView2 へ PostWebMessageAsString
    ///
    /// StdIO を使う理由:
    ///   - ポート番号の衝突リスクがない
    ///   - プロセスが死んだとき自動的に読み取りエラーで検知できる
    ///   - HTTP サーバーより軽量でセットアップが簡単
    ///
    /// node.exe の解決順序:
    ///   1. EXE 隣接の node-runtime/node.exe
    ///   2. PATH 上の node（開発環境向けフォールバック）
    ///
    /// エラーハンドリング:
    ///   - node.exe が見つからなければ警告ログを出して握りつぶす（アプリはクラッシュしない）
    ///   - サイドカーが予期せず終了した場合は自動再起動を試みる（最大 MaxRestartCount 回）
    /// </summary>
    public sealed class NodePlugin : IHostPlugin
    {
        // ---------------------------------------------------------------------------
        // 定数
        // ---------------------------------------------------------------------------

        private const string NodeRuntimeDir  = "node-runtime";
        private const string ServerScript    = "server.js";
        private const int    MaxRestartCount = 3;

        // ---------------------------------------------------------------------------
        // フィールド
        // ---------------------------------------------------------------------------

        private readonly WebView2             _webView;
        
        private          Process?             _nodeProcess;
        private          StreamWriter?        _stdin;
        private          int                  _restartCount = 0;
        private          bool                 _disposed;

        // StdIO 書き込みの排他制御
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        /// <summary>
        /// NodePlugin を生成する。
        /// PluginManager の汎用ローダーから Activator.CreateInstance(type, webView) で呼ばれる。
        /// </summary>
        public NodePlugin(WebView2 webView)
        {
            _webView = webView;
        }

        // ---------------------------------------------------------------------------
        // IHostPlugin
        // ---------------------------------------------------------------------------

        public string PluginName => "Node";

        /// <summary>
        /// PluginManager.Create から呼ばれる。node.exe プロセスを起動する。
        /// 失敗した場合は警告ログのみ（アプリはクラッシュしない）。
        /// </summary>
        public void Initialize()
        {
            TryStartNodeProcess();
        }

        /// <summary>
        /// source フィールドが "Node"（大文字小文字不問）のメッセージを受け取り
        /// Node.js サイドカーへ転送する。
        /// </summary>
        public void HandleWebMessage(string webMessageJson)
        {
            if (_disposed || string.IsNullOrWhiteSpace(webMessageJson) || _nodeProcess == null || _stdin == null) return;

            try
            {
                // JavaScriptSerializer でデシリアライズして source フィールドを確認する。
                // プラグイン DLL はペイロードが不定のため DataContractJsonSerializer ではなく
                // JavaScriptSerializer を採用している（ホスト EXE の固定スキーマとは分離）。
                var serializer = new JavaScriptSerializer();
                var msg = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(webMessageJson);
                if (msg == null || !msg.TryGetValue("source", out var srcObj) || 
                    !string.Equals(srcObj?.ToString(), "Node", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            // Node.js サイドカーへ非同期転送
            _ = SendToNodeAsync(webMessageJson);
        }

        // ---------------------------------------------------------------------------
        // Node.js プロセス管理
        // ---------------------------------------------------------------------------

        private void TryStartNodeProcess()
        {
            if (_disposed) return;
            AppLog.Log("INFO", "NodePlugin", "Node.js サイドカープロセスを起動しています...");

            var nodePath   = ResolveNodePath();
            var scriptPath = ResolveScriptPath();

            if (nodePath == null)
            {
                AppLog.Log("WARN", "NodePlugin",
                    "node.exe が見つかりません。Node.js 機能は無効です。\n" +
                    $"  検索場所: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, NodeRuntimeDir, "node.exe")}\n" +
                    "  または PATH 上の node");
                return;
            }

            if (scriptPath == null)
            {
                AppLog.Log("WARN", "NodePlugin",
                    $"{ServerScript} が見つかりません。Node.js 機能は無効です。\n" +
                    $"  検索場所: {Path.Combine(AppDomain.CurrentDomain.BaseDirectory, NodeRuntimeDir, ServerScript)}");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = nodePath,
                    Arguments              = $"\"{scriptPath}\"",
                    WorkingDirectory       = Path.GetDirectoryName(scriptPath)!,
                    UseShellExecute        = false,
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding  = new UTF8Encoding(false),
                };

                _nodeProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _nodeProcess.OutputDataReceived += OnNodeOutput;
                _nodeProcess.ErrorDataReceived  += OnNodeError;
                _nodeProcess.Exited             += OnNodeExited;

                _nodeProcess.Start();
                _nodeProcess.BeginOutputReadLine();
                _nodeProcess.BeginErrorReadLine();

                _stdin = new StreamWriter(_nodeProcess.StandardInput.BaseStream, new UTF8Encoding(false));

                AppLog.Log("INFO", "NodePlugin",
                    $"Node.js サイドカーを起動しました (PID: {_nodeProcess.Id})");
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "NodePlugin", "Node.js プロセスの起動に失敗しました", ex);
                _nodeProcess = null;
                _stdin       = null;
            }
        }

        private void OnNodeOutput(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            PostToJs(e.Data);
        }

        private void OnNodeError(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            AppLog.Log("WARN", "NodePlugin.Stderr", e.Data);
        }

        private void OnNodeExited(object sender, EventArgs e)
        {
            if (_disposed) return;

            var code = _nodeProcess?.ExitCode ?? -1;
            AppLog.Log("WARN", "NodePlugin",
                $"Node.js サイドカーが終了しました (ExitCode: {code})");

            if (_restartCount >= MaxRestartCount)
            {
                AppLog.Log("WARN", "NodePlugin",
                    $"再起動上限 ({MaxRestartCount}回) に達したため Node.js 機能を無効化します。");
                return;
            }

            _restartCount++;
            AppLog.Log("INFO", "NodePlugin",
                $"Node.js サイドカーを再起動します ({_restartCount}/{MaxRestartCount})...");

            // 少し待ってから再起動
            Task.Delay(1000).ContinueWith(_ => TryStartNodeProcess());
        }

        // ---------------------------------------------------------------------------
        // StdIO 通信
        // ---------------------------------------------------------------------------

        private async Task SendToNodeAsync(string json)
        {
            if (_stdin == null) return;
            await _writeLock.WaitAsync();
            try
            {
                // Node.js 側は改行区切りの NDJSON を期待する
                await _stdin.WriteLineAsync(json);
                await _stdin.FlushAsync();
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "NodePlugin.SendToNode", "Node.js への送信に失敗しました", ex);
            }
            finally
            {
                _writeLock.Release();
            }
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
                    AppLog.Log("WARN", "NodePlugin.PostToJs", "JS への投稿に失敗しました", ex);
                }
            }));
        }

        // ---------------------------------------------------------------------------
        // パス解決
        // ---------------------------------------------------------------------------

        private static string? ResolveNodePath()
        {
            // 1. node-runtime/node.exe（配布パッケージ用）
            var bundled = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, NodeRuntimeDir, "node.exe");
            if (File.Exists(bundled)) return bundled;

            // 2. PATH 上の node（開発環境フォールバック）
            return FindInPath("node.exe") ?? FindInPath("node");
        }

        private static string? ResolveScriptPath()
        {
            var path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, NodeRuntimeDir, ServerScript);
            return File.Exists(path) ? path : null;
        }

        private static string? FindInPath(string fileName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), fileName);
                    if (File.Exists(full)) return full;
                }
                catch { /* 無効なパスエントリは無視 */ }
            }
            return null;
        }

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _stdin?.Close(); }
            catch { }

            try
            {
                if (_nodeProcess != null && !_nodeProcess.HasExited)
                {
                    _nodeProcess.Kill();
                    _nodeProcess.WaitForExit(3000);
                    AppLog.Log("INFO", "NodePlugin", "Node.js サイドカーを終了しました");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("WARN", "NodePlugin.Dispose", "Node.js プロセスの終了に失敗しました", ex);
            }
            finally
            {
                _nodeProcess?.Dispose();
                _nodeProcess = null;
                _writeLock.Dispose();
            }
        }
    }
}
