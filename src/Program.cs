using System;
using System.Windows.Forms;

namespace WebView2AppHost
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // コンテンツプロバイダの初期化
            using (var zip = new ZipContentProvider())
            {
                if (!zip.Load())
                {
                    ShowErrorMessage("コンテンツが見つかりませんでした。");
                    return;
                }

                // 設定の読み込みとアプリ起動
                var config = LoadConfig(zip);
                Application.Run(new App(zip, config));
            }
        }

        private static AppConfig LoadConfig(ZipContentProvider zip)
        {
            using (var stream = zip.OpenEntry("/app.conf.json"))
            {
                return (stream != null) ? (AppConfig.Load(stream) ?? new AppConfig()) : new AppConfig();
            }
        }

        private static void ShowErrorMessage(string message)
        {
            MessageBox.Show(
                message + "\n\n" +
                "次のいずれかの配置になっているか確認してください。\n" +
                "- 個別配置: EXE と同じ場所に www フォルダを配置\n" +
                "- 外部指定: コマンドライン引数で ZIP パスを指定\n" +
                "- 同封: EXE と同名の .zip ファイルを配置\n" +
                "- 連結: bundle.py で EXE 末尾に ZIP を結合\n" +
                "- 埋め込み: プロジェクトのリソースとして埋め込み",
                "WebView2 App Host",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
