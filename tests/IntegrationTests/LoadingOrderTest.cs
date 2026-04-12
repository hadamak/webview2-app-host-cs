using System;
using System.IO;
using System.Text;
using WebView2AppHost;

namespace IntegrationTests
{
    class LoadingOrderTest
    {
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "";
            string mockExe = args.Length > 1 ? args[1] : "";
            string mockArgZip = args.Length > 2 ? args[2] : "";

            try
            {
                using (var provider = new ZipContentProvider(mockExe, mockArgZip))
                {
                    if (!provider.Load())
                    {
                        Console.WriteLine("FAIL: Provider failed to load any source.");
                        return 1;
                    }

                    // テスト判定ロジック
                    if (mode == "bundled_vs_loose")
                    {
                        // 連結 ZIP がある場合、www/app.conf.json は読めてはいけない
                        var config = provider.TryGetBytes("/app.conf.json");
                        string content = Encoding.UTF8.GetString(config ?? Array.Empty<byte>());
                        
                        if (content.Contains("BUNDLED_MARKER") && !content.Contains("LOOSE_MARKER"))
                        {
                            Console.WriteLine("SUCCESS: Bundled config won and loose config was blocked.");
                        }
                        else
                        {
                            Console.WriteLine($"FAIL: Wrong config loaded. Content: {content}");
                            return 1;
                        }

                        // ただしメディアファイル（txt等）は www/ から読めるべき
                        var media = provider.TryGetBytes("/media.txt");
                        if (media != null && Encoding.UTF8.GetString(media).Contains("MEDIA_MARKER"))
                        {
                            Console.WriteLine("SUCCESS: Loose media was accessible.");
                        }
                        else
                        {
                            Console.WriteLine("FAIL: Loose media was blocked.");
                            return 1;
                        }
                    }
                    else if (mode == "arg_vs_loose")
                    {
                        // 引数 ZIP が www/ より優先されるべき
                        var config = provider.TryGetBytes("/app.conf.json");
                        string content = Encoding.UTF8.GetString(config ?? Array.Empty<byte>());

                        if (content.Contains("ARG_MARKER"))
                        {
                            Console.WriteLine("SUCCESS: Arg ZIP won over loose files.");
                        }
                        else
                        {
                            Console.WriteLine($"FAIL: Arg ZIP did not override. Content: {content}");
                            return 1;
                        }
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: {ex.Message}");
                return 1;
            }
        }
    }
}
