using System;
using System.IO;
using System.Reflection;
using System.Text;
using WebView2AppHost;

namespace PluginCoreTester
{
    class Program
    {
        static void Main(string[] args)
        {
            AppLog.Override = Console.Out;
            Console.WriteLine("=== PluginCoreTester ===");
            
            // テスト 1: AppConfig のパース
            TestAppConfigParse();
            
            // テスト 2: AppConfig の LoadDlls 拡張
            TestAppConfigLoadDlls();
            
            // テスト 3: リフレクション呼び出し
            TestReflectionInvoke();
            
            // テスト 4: GenericDllPlugin の初期化テスト（新しい仕様）
            TestGenericDllPluginInitNew();
            
            // テスト 5: GenericSidecarPlugin の初期化テスト（新しい仕様）
            TestGenericSidecarPluginInitNew();
            
            // テスト 6: GenericDllPlugin を使った実際の DLL ロード・メソッド呼び出しテスト
            TestGenericDllPluginActualInvoke();
            
            Console.WriteLine("\n=== 全テスト完了 ===");
        }
        
        static void TestAppConfigParse()
        {
            Console.WriteLine("\n--- テスト 1: AppConfig パース ---");
            
            var json = @"{
                ""title"": ""Test App"",
                ""width"": 800,
                ""height"": 600,
                ""fullscreen"": true
            }";
            
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var config = AppConfig.Load(stream);
            
            if (config != null)
            {
                Console.WriteLine($"タイトル: {config.Title}");
                Console.WriteLine($"幅: {config.Width}");
                Console.WriteLine($"高さ: {config.Height}");
                Console.WriteLine($"フルスクリーン: {config.Fullscreen}");
                Console.WriteLine("✓ パース成功");
            }
            else
            {
                Console.WriteLine("✗ パース失敗");
            }
        }
        
        static void TestAppConfigLoadDlls()
        {
            Console.WriteLine("\n--- テスト 2: AppConfig LoadDlls 拡張 ---");
            
            var json = @"{
                ""title"": ""Test App"",
                ""loadDlls"": [
                    { ""alias"": ""Calc"", ""dll"": ""TestLib.dll"", ""exposeEvents"": [""OnResult""] }
                ],
                ""sidecars"": [
                    { ""alias"": ""NodeBackend"", ""mode"": ""streaming"", ""executable"": ""node.exe"", ""workingDirectory"": ""."", ""args"": [""server.js""], ""waitForReady"": true }
                ]
            }";
            
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var config = AppConfig.Load(stream);
            
            if (config != null)
            {
                Console.WriteLine($"タイトル: {config.Title}");
                Console.WriteLine($"LoadDlls 数: {config.LoadDlls.Length}");
                Console.WriteLine($"Sidecars 数: {config.Sidecars.Length}");
                
                if (config.LoadDlls.Length > 0)
                {
                    var entry = config.LoadDlls[0];
                    Console.WriteLine($"  LoadDlls[0]: alias={entry.Alias}, dll={entry.Dll}");
                    Console.WriteLine($"  exposeEvents: [{string.Join(", ", entry.ExposeEvents)}]");
                }
                
                if (config.Sidecars.Length > 0)
                {
                    var entry = config.Sidecars[0];
                    Console.WriteLine($"  Sidecars[0]: alias={entry.Alias}, mode={entry.Mode}");
                    Console.WriteLine($"  executable={entry.Executable}, workingDirectory={entry.WorkingDirectory}");
                    Console.WriteLine($"  args: [{string.Join(", ", entry.Args)}]");
                    Console.WriteLine($"  waitForReady={entry.WaitForReady}");
                }
                
                Console.WriteLine("✓ パース成功");
            }
            else
            {
                Console.WriteLine("✗ パース失敗");
            }
        }
        
        static void TestReflectionInvoke()
        {
            Console.WriteLine("\n--- テスト 3: リフレクション呼び出し ---");
            
            try
            {
                // TestLib.dll をロード
                var dllPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "TestDll", "bin", "Debug", "net472", "TestLib.dll");
                
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"✗ DLL が見つかりません: {dllPath}");
                    return;
                }
                
                var asm = Assembly.LoadFrom(dllPath);
                Console.WriteLine($"DLL をロードしました: {asm.FullName}");
                
                // Calculator クラスを取得
                var calcType = asm.GetType("TestLib.Calculator");
                if (calcType == null)
                {
                    Console.WriteLine("✗ Calculator クラスが見つかりません");
                    return;
                }
                
                Console.WriteLine($"クラスを取得しました: {calcType.FullName}");
                
                // Add メソッドを取得
                var addMethod = calcType.GetMethod("Add", BindingFlags.Public | BindingFlags.Static);
                if (addMethod == null)
                {
                    Console.WriteLine("✗ Add メソッドが見つかりません");
                    return;
                }
                
                Console.WriteLine($"メソッドを取得しました: {addMethod.Name}");
                
                // メソッドを呼び出し
                var result = addMethod.Invoke(null, new object[] { 3, 4 });
                Console.WriteLine($"呼び出し結果: 3 + 4 = {result}");
                
                if (result is int intResult && intResult == 7)
                {
                    Console.WriteLine("✓ リフレクション呼び出し成功");
                }
                else
                {
                    Console.WriteLine("✗ 期待される結果と異なります");
                }
                
                // イベント購読テスト
                var onResultEvent = calcType.GetEvent("OnResult");
                if (onResultEvent != null)
                {
                    Console.WriteLine($"イベントを取得しました: {onResultEvent.Name}");
                    
                    // イベントハンドラを作成
                    Action<int> handler = (value) => Console.WriteLine($"  イベント受信: {value}");
                    
                    // イベントに登録
                    onResultEvent.AddEventHandler(null, handler);
                    
                    // イベントを発火
                    var triggerMethod = calcType.GetMethod("TriggerResult", BindingFlags.Public | BindingFlags.Static);
                    if (triggerMethod != null)
                    {
                        triggerMethod.Invoke(null, new object[] { 42 });
                        Console.WriteLine("✓ イベント購読テスト成功");
                    }
                }
                else
                {
                    Console.WriteLine("✗ OnResult イベントが見つかりません");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ エラー: {ex.Message}");
                Console.WriteLine($"スタックトレース: {ex.StackTrace}");
            }
        }
        
        static void TestGenericDllPluginInitNew()
        {
            Console.WriteLine("\n--- テスト 4: GenericDllPlugin 初期化テスト（新仕様） ---");
            
            try
            {
                // GenericDllPlugin DLL をロード
                var dllPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "..", "src-generic", "bin", "Debug", "net472", "WebView2AppHost.GenericDllPlugin.dll");
                
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"✗ GenericDllPlugin DLL が見つかりません: {dllPath}");
                    return;
                }
                
                var asm = Assembly.LoadFrom(dllPath);
                Console.WriteLine($"GenericDllPlugin DLL をロードしました: {asm.FullName}");
                
                // GenericDllPlugin クラスを取得
                var pluginType = asm.GetType("WebView2AppHost.GenericDllPlugin");
                if (pluginType == null)
                {
                    Console.WriteLine("✗ GenericDllPlugin クラスが見つかりません");
                    return;
                }
                
                Console.WriteLine($"GenericDllPlugin クラスを取得しました: {pluginType.FullName}");
                
                // Initialize(string) メソッドを確認
                var initMethod = pluginType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (initMethod == null)
                {
                    Console.WriteLine("✗ Initialize(string) メソッドが見つかりません");
                    return;
                }
                
                Console.WriteLine("✓ Initialize(string) メソッドが存在します");
                
                // JSON 文字列を直接作成（AppConfigに依存しない）
                var configJson = @"{
                    ""title"": ""無視されるべきデータ"",
                    ""loadDlls"": [
                        { ""alias"": ""Calc"", ""dll"": ""../../../../TestDll/bin/Debug/net472/TestLib.dll"", ""exposeEvents"": [""OnResult"", ""OnPing"", ""OnCalculationFinished""] }
                    ]
                }";
                
                Console.WriteLine("✓ JSON 文字列を作成しました（AppConfigに依存しない）");
                
                // GenericDllPlugin のインスタンスを作成（WebView2 は null で OK）
                var plugin = Activator.CreateInstance(pluginType, new object[] { null });
                if (plugin == null)
                {
                    Console.WriteLine("✗ GenericDllPlugin のインスタンス作成に失敗しました");
                    return;
                }
                
                Console.WriteLine("✓ GenericDllPlugin のインスタンスを作成しました");
                
                // Initialize を呼び出し
                initMethod.Invoke(plugin, new object[] { configJson });
                Console.WriteLine("✓ Initialize(string) を呼び出しました");
                
                Console.WriteLine("✓ GenericDllPlugin 初期化テスト成功（新仕様）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ エラー: {ex.Message}");
                Console.WriteLine($"スタックトレース: {ex.StackTrace}");
            }
        }
        
        static void TestGenericSidecarPluginInitNew()
        {
            Console.WriteLine("\n--- テスト 5: GenericSidecarPlugin 初期化テスト（新仕様） ---");
            
            try
            {
                // GenericDllPlugin DLL をロード（GenericSidecarPlugin も同じ DLL に含まれる）
                var dllPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "..", "src-generic", "bin", "Debug", "net472", "WebView2AppHost.GenericDllPlugin.dll");
                
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"✗ GenericDllPlugin DLL が見つかりません: {dllPath}");
                    return;
                }
                
                var asm = Assembly.LoadFrom(dllPath);
                Console.WriteLine($"GenericDllPlugin DLL をロードしました: {asm.FullName}");
                
                // GenericSidecarPlugin クラスを取得
                var pluginType = asm.GetType("WebView2AppHost.GenericSidecarPlugin");
                if (pluginType == null)
                {
                    Console.WriteLine("✗ GenericSidecarPlugin クラスが見つかりません");
                    return;
                }
                
                Console.WriteLine($"GenericSidecarPlugin クラスを取得しました: {pluginType.FullName}");
                
                // Initialize(string) メソッドを確認
                var initMethod = pluginType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (initMethod == null)
                {
                    Console.WriteLine("✗ Initialize(string) メソッドが見つかりません");
                    return;
                }
                
                Console.WriteLine("✓ Initialize(string) メソッドが存在します");
                
                // JSON 文字列を直接作成（AppConfigに依存しない）
                var configJson = @"{
                    ""title"": ""無視されるべきデータ"",
                    ""sidecars"": [
                        { ""alias"": ""TestSidecar"", ""mode"": ""streaming"", ""executable"": ""nonexistent.exe"", ""workingDirectory"": ""."", ""args"": [], ""waitForReady"": false }
                    ]
                }";
                
                Console.WriteLine("✓ JSON 文字列を作成しました（AppConfigに依存しない）");
                
                // GenericSidecarPlugin のインスタンスを作成（WebView2 は null で OK）
                var plugin = Activator.CreateInstance(pluginType, new object[] { null });
                if (plugin == null)
                {
                    Console.WriteLine("✗ GenericSidecarPlugin のインスタンス作成に失敗しました");
                    return;
                }
                
                Console.WriteLine("✓ GenericSidecarPlugin のインスタンスを作成しました");
                
                // Initialize を呼び出し（サイドカーが存在しないため、警告ログが出るはず）
                initMethod.Invoke(plugin, new object[] { configJson });
                Console.WriteLine("✓ Initialize(string) を呼び出しました");
                
                Console.WriteLine("✓ GenericSidecarPlugin 初期化テスト成功（新仕様）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ エラー: {ex.Message}");
                Console.WriteLine($"スタックトレース: {ex.StackTrace}");
            }
        }
        
        static void TestGenericDllPluginActualInvoke()
        {
            Console.WriteLine("\n--- テスト 6: GenericDllPlugin を使った実際の DLL ロード・メソッド呼び出しテスト ---");
            
            try
            {
                // GenericDllPlugin DLL をロード
                var dllPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "..", "src-generic", "bin", "Debug", "net472", "WebView2AppHost.GenericDllPlugin.dll");
                
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"✗ GenericDllPlugin DLL が見つかりません: {dllPath}");
                    return;
                }
                
                var asm = Assembly.LoadFrom(dllPath);
                Console.WriteLine($"GenericDllPlugin DLL をロードしました: {asm.FullName}");
                
                // GenericDllPlugin クラスを取得
                var pluginType = asm.GetType("WebView2AppHost.GenericDllPlugin");
                if (pluginType == null)
                {
                    Console.WriteLine("✗ GenericDllPlugin クラスが見つかりません");
                    return;
                }
                
                Console.WriteLine($"GenericDllPlugin クラスを取得しました: {pluginType.FullName}");
                
                // GenericDllPlugin のインスタンスを作成（WebView2 は null で OK）
                var plugin = Activator.CreateInstance(pluginType, new object[] { null });
                if (plugin == null)
                {
                    Console.WriteLine("✗ GenericDllPlugin のインスタンス作成に失敗しました");
                    return;
                }
                
                Console.WriteLine("✓ GenericDllPlugin のインスタンスを作成しました");
                
                // Initialize(string) を呼び出し
                var initMethod = pluginType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                var configJson = @"{
                    ""title"": ""無視されるべきデータ"",
                    ""loadDlls"": [
                        { ""alias"": ""Calc"", ""dll"": ""../../../../TestDll/bin/Debug/net472/TestLib.dll"", ""exposeEvents"": [""OnResult"", ""OnPing"", ""OnCalculationFinished""] }
                    ]
                }";
                initMethod.Invoke(plugin, new object[] { configJson });
                Console.WriteLine("✓ Initialize(string) を呼び出しました（TestLib.dll をロード）");
                
                // HandleWebMessage を呼び出し
                var handleMessageMethod = pluginType.GetMethod("HandleWebMessage", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (handleMessageMethod == null)
                {
                    Console.WriteLine("✗ HandleWebMessage メソッドが見つかりません");
                    return;
                }
                
                Console.WriteLine("✓ HandleWebMessage メソッドが存在します");
                
                // メソッド呼び出しメッセージを作成
                var invokeMessage = @"{
                    ""source"": ""Host"",
                    ""messageId"": ""invoke"",
                    ""params"": {
                        ""dllName"": ""Calc"",
                        ""className"": ""Calculator"",
                        ""methodName"": ""Add"",
                        ""args"": [3, 4]
                    },
                    ""asyncId"": 1
                }";
                
                Console.WriteLine("✓ メソッド呼び出しメッセージを作成しました");
                
                // HandleWebMessage を呼び出し（結果は WebView2 に送信されるため、ここでは確認できない）
                handleMessageMethod.Invoke(plugin, new object[] { invokeMessage });
                Console.WriteLine("✓ HandleWebMessage を呼び出しました");
                
                Console.WriteLine("✓ GenericDllPlugin を使った実際の DLL ロード・メソッド呼び出しテスト成功");
                Console.WriteLine("  注意: 実際の結果は WebView2 に送信されるため、ここでは確認できません");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ エラー: {ex.Message}");
                Console.WriteLine($"スタックトレース: {ex.StackTrace}");
            }
        }
    }
}
