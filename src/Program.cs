using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WebView2AppHost
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                bool isMcpMode = Array.IndexOf(args, "--mcp-headless") >= 0 || Array.IndexOf(args, "--mcp-proxy") >= 0;

                // MCP モードの時だけ BOM なし UTF-8 を設定する
                if (isMcpMode)
                {
                    var encoding = new System.Text.UTF8Encoding(false);
                    try
                    {
                        Console.InputEncoding  = encoding;
                        Console.OutputEncoding = encoding;
                    }
                    catch { /* コンソールがない場合は無視 */ }
                }

                // --mcp-headless: WebView2 を起動せず MCP サーバーとして動作する
                if (Array.IndexOf(args, "--mcp-headless") >= 0)
                {
                    RunMcpHeadless();
                    return;
                }

                // --mcp-proxy: Named Pipe 経由で本体プロセスに中継する軽量プロキシ
                if (Array.IndexOf(args, "--mcp-proxy") >= 0)
                {
                    RunMcpProxy();
                    return;
                }

                // 通常モード（WebView2 あり）
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var zip = new ZipContentProvider())
                {
                    if (!zip.Load())
                    {
                        ShowErrorMessage("コンテンツが見つかりませんでした。");
                        return;
                    }

                    var config = LoadConfig(zip);
                    Application.Run(new App(zip, config));
                }
            }
            catch (Exception ex)
            {
                // 致命的なエラーを stderr に出力（MCP クライアントがエラーを拾えるように）
                Console.Error.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                
                // 通常モードならダイアログも出す
                if (Array.IndexOf(args, "--mcp-headless") < 0 && Array.IndexOf(args, "--mcp-proxy") < 0)
                {
                    MessageBox.Show(ex.Message, "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Environment.Exit(1);
            }
        }

        // -------------------------------------------------------------------
        // Mode 1: WebView2 なし MCP サーバー
        // -------------------------------------------------------------------

        /// <summary>
        /// WebView2 を起動せず、app.conf.json のプラグインだけを使って
        /// stdin/stdout で MCP サーバーとして動作する。
        /// </summary>
        private static void RunMcpHeadless()
        {
            // ログを stderr に向ける（stdout は MCP 通信専用）
            AppLog.Override = Console.Error;
            AppLog.Log("INFO", "Program", "MCP ヘッドレスモードで起動します");

            try
            {
                var config = LoadConfigFromExeDir();
                var cts    = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                var (bus, mcp) = ConnectorFactory.BuildHeadless(config, cts.Token);
                using (bus)
                {
                    AppLog.Log("INFO", "Program", "MCP コネクター起動（stdin/stdout）");
                    mcp.RunAsync(cts.Token).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "Program", "ヘッドレスモードで例外が発生しました", ex);
                throw;
            }

            AppLog.Log("INFO", "Program", "MCP ヘッドレスモード終了");
        }

        // -------------------------------------------------------------------
        // Mode: --mcp-proxy
        // -------------------------------------------------------------------

        /// <summary>
        /// 軽量プロキシプロセスとして動作する。
        /// </summary>
        private static void RunMcpProxy()
        {
            AppLog.Override = Console.Error;
            AppLog.Log("INFO", "Program", "MCP プロキシモードで起動します");

            try
            {
                var config    = LoadConfigFromExeDir();
                var pipeName  = ConnectorFactory.GetPipeName();
                var serverExe = ConnectorFactory.GetServerExePath();
                var cts       = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

                // ローカルバス: McpConnector（stdio）↔ PipeClientConnector（Named Pipe）
                var bus    = new MessageBus();
                var mcp    = new McpConnector(config, callTimeout: TimeSpan.FromSeconds(30));
                var client = new PipeClientConnector(pipeName, serverExe);

                bus.Register(mcp);
                bus.Register(client);

                using (bus)
                {
                    AppLog.Log("INFO", "Program", $"パイプ接続先: \\\\.\\pipe\\{pipeName}");

                    var pipeTask = Task.Run(() => client.RunAsync(cts.Token), cts.Token);
                    var mcpTask  = mcp.RunAsync(cts.Token);

                    Task.WhenAny(pipeTask, mcpTask)
                        .ContinueWith(_ => cts.Cancel());

                    mcpTask.GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                AppLog.Log("ERROR", "Program", "プロキシモードで例外が発生しました", ex);
                throw;
            }

            AppLog.Log("INFO", "Program", "MCP プロキシ終了");
        }

        // -------------------------------------------------------------------
        // 設定読み込み
        // -------------------------------------------------------------------

        private static AppConfig LoadConfig(ZipContentProvider zip)
        {
            using (var stream = zip.OpenEntry("/app.conf.json"))
            {
                var config = (stream != null)
                    ? (AppConfig.Load(stream) ?? new AppConfig())
                    : new AppConfig();

                var exeDir = GetExeDir();
                config.ApplyUserConfig(exeDir);
                return config;
            }
        }

        /// <summary>
        /// ヘッドレスモード用の設定読み込み。
        /// EXE 隣接の app.conf.json または www/app.conf.json を探す。
        /// 見つからない場合はデフォルト値を返す。
        /// </summary>
        private static AppConfig LoadConfigFromExeDir()
        {
            var exeDir = GetExeDir();

            // 優先順位: www/app.conf.json → app.conf.json → デフォルト
            var candidates = new[]
            {
                Path.Combine(exeDir, "www", "app.conf.json"),
                Path.Combine(exeDir, "app.conf.json"),
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;

                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var config = AppConfig.Load(stream);
                    if (config != null)
                    {
                        AppLog.Log("INFO", "Program", $"設定を読み込みました: {path}");
                        config.ApplyUserConfig(exeDir);
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Log("WARN", "Program", $"設定の読み込みに失敗（スキップ）: {path}", ex);
                }
            }

            AppLog.Log("INFO", "Program", "app.conf.json が見つかりません。デフォルト設定を使用します。");
            return new AppConfig();
        }

        private static string GetExeDir() =>
            Path.GetDirectoryName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!) ?? ".";

        // -------------------------------------------------------------------
        // エラー表示（Mode 2 のみ）
        // -------------------------------------------------------------------

        private static void ShowErrorMessage(string message)
        {
            MessageBox.Show(
                message + "\n\n" +
                "次のいずれかの配置になっているか確認してください。\n" +
                "- 個別配置: EXE と同じ場所に www フォルダを配置\n" +
                "- 外部指定: コマンドライン引数で ZIP パスを指定\n" +
                "- 同封: EXE と同名の .zip ファイルを配置\n" +
                "- 連結: copy /b コマンド等で EXE 末尾に ZIP を結合\n" +
                "- 埋め込み: プロジェクトのリソースとして埋め込み",
                "WebView2 App Host",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
