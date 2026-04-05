WebView2 App Host - Node.js サポート (汎用サイドカープラグイン経由)
=============================================================

本プロジェクトでは、汎用サイドカープラグイン (GenericSidecarPlugin) を使用して
Node.js と連携できます。node.exe をサイドカーとして起動し、JSON で通信します。

■ 導入方法
1. WebView2AppHost.exe と同じフォルダに以下のファイルを配置します。
   - WebView2AppHost.GenericSidecarPlugin.dll
   - node-runtime/ フォルダ (node.exe とスクリプトを含む)

2. app.conf.json を設定します。
   {
     "plugins": ["GenericSidecarPlugin"],
     "sidecars": [
       {
         "alias": "NodeBackend",
         "executable": "node-runtime/node.exe",
         "args": ["server.js"]
       }
     ]
   }

3. JavaScript から通信します（JSON-RPC 2.0）。
   window.chrome.webview.postMessage(JSON.stringify({
     jsonrpc: "2.0",
     id: 1,
     method: "NodeBackend.Node.version",
     params: []
   }));

   あるいは同梱の host.js を使うと、より簡単に呼び出せます。
   const version = await Host.NodeBackend.Node.version();

■ 詳細ドキュメント
詳細は以下を参照してください。
- docs/generic-sidecar-plugin.md (プラグインの仕様)
