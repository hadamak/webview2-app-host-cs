WebView2 App Host - Steam 連携 (汎用 DLL プラグイン経由)
====================================================

本プロジェクトでは、汎用 DLL プラグイン (GenericDllPlugin) を使用して
Steamworks と連携できます。Facepunch.Steamworks の API を JS から直接呼び出せます。

■ 導入方法
1. WebView2AppHost.exe と同じフォルダに以下のファイルを配置します。
   - WebView2AppHost.GenericDllPlugin.dll
   - Facepunch.Steamworks.Win64.dll
   - steam_api64.dll

2. app.conf.json を設定します。
   {
     "plugins": ["GenericDllPlugin"],
     "loadDlls": ["Facepunch.Steamworks.Win64.dll"]
   }

3. JavaScript から呼び出します。
   await Host.invoke({
     dllName: "Facepunch.Steamworks.Win64",
     className: "SteamClient",
     methodName: "Init",
     args: [480]
   });

■ 詳細ドキュメント
詳細は以下を参照してください。
- docs/generic-dll-plugin.md (プラグインの仕様)
- docs/steam/overview.md (Steam 連携ガイド)
