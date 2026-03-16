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

            // ZIP を開く
            var zip = new ZipContentProvider();
            if (!zip.Load())
            {
                MessageBox.Show(
                    "コンテンツが見つかりませんでした。\n" +
                    "次のいずれかの配置になっているか確認してください。\n" +
                    "- 個別配置: EXE と同じ場所に www フォルダを配置\n" +
                    "- 外部指定: コマンドライン引数で ZIP パスを指定\n" +
                    "- 同封: EXE と同名の .zip ファイルを配置\n" +
                    "- 連結: bundle.py で EXE 末尾に ZIP を結合\n" +
                    "- 埋め込み: プロジェクトのリソースとして埋め込み",
                    "WebView2 App Host",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            // 設定を読む
            var config = AppConfig.Load(zip);

            // アプリ起動
            Application.Run(new App(zip, config));
        }
    }
}
