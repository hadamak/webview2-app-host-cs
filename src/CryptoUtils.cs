using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace WebView2AppHost
{
    /// <summary>
    /// AES-256 による透過的な復号を提供するユーティリティ。
    /// .wve 拡張子のファイル（マジックナンバー WVAE）を扱います。
    /// </summary>
    internal static class CryptoUtils
    {
        // 4バイトのマジックナンバー: 'W' 'V' 'A' 'E' (WebView2 App Encrypted)
        private static readonly byte[] MagicWve = { 0x57, 0x56, 0x41, 0x45 };

        // デフォルトの共通鍵 (32 bytes = 256 bits)
        // 開発環境に応じてここを上書き、あるいは別ファイル等から読み込むよう拡張可能です。
        private static readonly byte[] DefaultKey = 
        {
            0x4a, 0x6e, 0x39, 0x76, 0x2d, 0x31, 0x32, 0x33,
            0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x30, 0x61,
            0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f, 0x70, 0x71
        };

        /// <summary>
        /// 指定したストリームが .wve マジックナンバーで始まっているか確認する。
        /// </summary>
        public static bool IsWveHeader(Stream stream)
        {
            if (!stream.CanRead || stream.Length < MagicWve.Length) return false;
            
            var currentPos = stream.Position;
            var header = new byte[MagicWve.Length];
            int read = stream.Read(header, 0, MagicWve.Length);
            stream.Position = currentPos; // シークを戻す

            return read == MagicWve.Length && header.SequenceEqual(MagicWve);
        }

        /// <summary>
        /// .wve 形式のストリームから復号済みストリーム（MemoryStream）を生成する。
        /// .NET Framework 4.7.2 の CryptoStream は Length/Seek をサポートしないため、
        /// シーク可能な MemoryStream に一度すべて展開して返します。
        /// </summary>
        public static Stream CreateDecryptStream(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            try
            {
                // ヘッダー読み取り [Magic(4)] [Mode(1)] [IV(16)]
                byte[] header = new byte[MagicWve.Length + 1 + 16];
                int read = input.Read(header, 0, header.Length);

                if (read < header.Length || !header.Take(4).SequenceEqual(MagicWve))
                {
                    throw new InvalidDataException("Invalid WVE header.");
                }

                int mode = header[4];
                if (mode != 0) // 0: Simple AES
                {
                    throw new NotSupportedException($"Unsupported encryption mode: {mode}");
                }

                byte[] iv = new byte[16];
                Array.Copy(header, 5, iv, 0, 16);

                using (var aes = Aes.Create())
                {
                    aes.Key = DefaultKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var decryptor = aes.CreateDecryptor())
                    using (var cryptoStream = new CryptoStream(input, decryptor, CryptoStreamMode.Read))
                    {
                        var ms = new MemoryStream();
                        cryptoStream.CopyTo(ms);
                        ms.Position = 0;
                        return ms;
                    }
                }
            }
            finally
            {
                // CryptoStreamMode.Read で wrapping した場合、
                // 外側の Dispose で内側も Dispose されるが、
                // ここでは MemoryStream へコピーし終えたので入力を明示的に閉じる。
                input.Dispose();
            }
        }

        /// <summary>
        /// 指定したデータを暗号化し、.wve 形式でラップして返す。
        /// 主にツールやテストで使用。
        /// </summary>
        public static byte[] Encrypt(byte[] plainData)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = DefaultKey;
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var ms = new MemoryStream())
                {
                    // Header
                    ms.Write(MagicWve, 0, MagicWve.Length);
                    ms.WriteByte(0); // Mode 0
                    ms.Write(aes.IV, 0, aes.IV.Length);

                    // Body
                    using (var encryptor = aes.CreateEncryptor())
                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    {
                        cs.Write(plainData, 0, plainData.Length);
                        cs.FlushFinalBlock();
                    }
                    return ms.ToArray();
                }
            }
        }
    }
}
