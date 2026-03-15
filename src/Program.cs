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
                    "- コマンドライン引数で ZIP を指定\n" +
                    "- EXE と同名の .zip を同じフォルダに配置\n" +
                    "- EXE 末尾に ZIP を結合\n" +
                    "- EXE にコンテンツを埋め込み",
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
