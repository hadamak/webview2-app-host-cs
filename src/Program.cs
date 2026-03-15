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
                    "app.zip が EXE と同じフォルダにあるか、\n" +
                    "EXE にコンテンツが埋め込まれているか確認してください。",
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
