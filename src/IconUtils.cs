using System;
using System.Drawing;
using System.IO;

namespace WebView2AppHost
{
    /// <summary>
    /// アイコン操作に関するユーティリティクラス。
    /// </summary>
    internal static class IconUtils
    {
        /// <summary>
        /// 32x32 Bitmap を ICO 形式で Stream に書き出す。
        /// Icon コンストラクタは ICO 形式の Stream を要求するため。
        /// </summary>
        public static void WriteIco(Bitmap bmp, Stream dest)
        {
            using (var pngStream = new MemoryStream())
            {
                bmp.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
                var pngBytes = pngStream.ToArray();

                // ICO ヘッダ構造: ICONDIR(6) + ICONDIRENTRY(16) + PNG data
                using (var w = new BinaryWriter(dest, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    w.Write((short)0);               // Reserved
                    w.Write((short)1);               // Type: 1 = ICO
                    w.Write((short)1);               // Count: 1 image
                    // ICO 仕様: 256px 以上は 0 を書く（255 を超えると byte にキャストすると
                    // 意図しない値になるため、明示的に判定する）。
                    w.Write(bmp.Width  >= 256 ? (byte)0 : (byte)bmp.Width);   // Width
                    w.Write(bmp.Height >= 256 ? (byte)0 : (byte)bmp.Height);  // Height
                    w.Write((byte)0);                // ColorCount
                    w.Write((byte)0);                // Reserved
                    w.Write((short)1);               // Planes
                    w.Write((short)32);              // BitCount
                    w.Write((int)pngBytes.Length);  // SizeInBytes
                    w.Write((int)22);               // Offset: 6 + 16 = 22
                    w.Write(pngBytes);
                }
            }
        }

        /// <summary>
        /// 現在のプロセス（EXE）のアイコンを取得する。
        /// </summary>
        public static Icon GetAppIcon()
        {
            return Icon.ExtractAssociatedIcon(
                System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
        }
    }
}
