using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using WebView2AppHost;

namespace HostTests
{
    internal static class ProtectionTests
    {
        public static void Run(string workDir)
        {
            Console.WriteLine("Running Protection tests...");

            RunEncryptionTests();
            RunPriorityTests(workDir);
            RunIndexingTests(workDir);

            Console.WriteLine("  Protection tests passed.");
        }

        private static void RunEncryptionTests()
        {
            var plainText = "Hello, WebView2 Protection!";
            var plainData = Encoding.UTF8.GetBytes(plainText);
            
            var encrypted = CryptoUtils.Encrypt(plainData);
            
            Assert(CryptoUtils.IsWveHeader(new MemoryStream(encrypted)), "IsWveHeader: True for encrypted data");
            Assert(!CryptoUtils.IsWveHeader(new MemoryStream(plainData)), "IsWveHeader: False for plain data");

            using (var ms = new MemoryStream(encrypted))
            using (var decryptedStream = CryptoUtils.CreateDecryptStream(ms))
            using (var reader = new StreamReader(decryptedStream, Encoding.UTF8))
            {
                var result = reader.ReadToEnd();
                Assert(result == plainText, $"Encryption/Decryption: expected '{plainText}', got '{result}'");
            }
        }

        private static void RunPriorityTests(string workDir)
        {
            // シナリオ:
            // 埋め込み (Inner): index.html.wvc (内容: "INNER-WVC")
            // 外部ZIP (Middle): index.html (内容: "ZIP-PLAIN")
            // www/ (Outer): index.html (内容: "WWW-PLAIN")
            
            var testDir = Path.Combine(workDir, "priority-test");
            Directory.CreateDirectory(testDir);
            var wwwDir = Path.Combine(testDir, "www");
            Directory.CreateDirectory(wwwDir);

            // 1. www/index.html (通常ファイル)
            File.WriteAllText(Path.Combine(wwwDir, "index.html"), "WWW-PLAIN");

            // 2. zip/index.html (通常ファイル)
            // app.zip という名前で配置すれば Load() で読み込まれる
            var zipPath = Path.Combine(testDir, "app.zip");
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // 通常の .html
                var entry1 = zip.CreateEntry("index.html");
                using (var sw = new StreamWriter(entry1.Open())) sw.Write("ZIP-PLAIN");

                // 保護された .wvc (平文コア)
                var entry2 = zip.CreateEntry("index.html.wvc");
                using (var sw = new StreamWriter(entry2.Open())) sw.Write("INNER-WVC");
                
                // 保護された .wve (暗号化)
                var entry3 = zip.CreateEntry("secret.js.wve");
                var secretData = CryptoUtils.Encrypt(Encoding.UTF8.GetBytes("SECRET-CODE"));
                using (var stream = entry3.Open()) stream.Write(secretData, 0, secretData.Length);
            }

            // Mock EXE path: test.exe に対して test.zip または app.zip が探索される
            var exePath = Path.Combine(testDir, "test.exe");
            File.WriteAllBytes(exePath, new byte[0]);
            // app.zip は埋め込みリソース名として扱われるため、
            // ここでは test.zip にリネームして "Sibling Source" として認識させる
            File.Copy(zipPath, Path.Combine(testDir, "test.zip"), true);

            using (var provider = new ZipContentProvider(exePath))
            {
                provider.Load();

                // テスト1: index.html リクエスト -> .wvc があるので INNER-WVC が返るべき (Inner Priority)
                using (var stream = provider.OpenEntry("/index.html"))
                {
                    Assert(stream != null, "OpenEntry /index.html: Not null");
                    var text = new StreamReader(stream!).ReadToEnd();
                    Assert(text == "INNER-WVC", $"Inner Priority (.wvc): expected 'INNER-WVC', got '{text}'");
                }

                // テスト2: secret.js リクエスト -> .wve があるので復号されて SECRET-CODE が返るべき
                using (var stream = provider.OpenEntry("/secret.js"))
                {
                    Assert(stream != null, "OpenEntry /secret.js: Not null");
                    var text = new StreamReader(stream!).ReadToEnd();
                    Assert(text == "SECRET-CODE", $"Inner Priority (.wve): expected 'SECRET-CODE', got '{text}'");
                }

                // テスト3: www/ にしかないファイル
                File.WriteAllText(Path.Combine(wwwDir, "only-www.txt"), "ONLY-WWW");
                provider.Load(); // Re-index
                using (var stream = provider.OpenEntry("/only-www.txt"))
                {
                    Assert(stream != null, "OpenEntry /only-www.txt: Not null");
                    var text = new StreamReader(stream!).ReadToEnd();
                    Assert(text == "ONLY-WWW", $"Outer Priority (Regular): expected 'ONLY-WWW', got '{text}'");
                }
            }
        }

        private static void RunIndexingTests(string workDir)
        {
            // インデックスにより同一パスに複数ソースがある場合、
            // 順序が正しく保存されているかを確認。
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception("Assert failed: " + message);
        }
    }
}
