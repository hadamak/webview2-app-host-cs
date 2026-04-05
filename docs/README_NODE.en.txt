WebView2 App Host - Node.js Support (via GenericSidecarPlugin)
=============================================================

This project supports Node.js integration using the GenericSidecarPlugin.
You can run node.exe as a sidecar process and communicate via JSON.

■ How to use
1. Place the following files next to WebView2AppHost.exe:
   - WebView2AppHost.GenericSidecarPlugin.dll
   - node-runtime/ folder (containing node.exe and your scripts)

2. Configure app.conf.json:
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

3. Communicate from JavaScript (JSON-RPC 2.0):
   window.chrome.webview.postMessage(JSON.stringify({
     jsonrpc: "2.0",
     id: 1,
     method: "NodeBackend.Node.version",
     params: []
   }));

   Or use the bundled host.js helper for a simpler call style:
   const version = await Host.NodeBackend.Node.version();

■ Documentation
For more details, see:
- docs/generic-sidecar-plugin.md (Plugin specification)
