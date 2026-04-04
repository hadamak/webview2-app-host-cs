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

3. Communicate from JavaScript:
   window.chrome.webview.postMessage({
     source: "NodeBackend",
     method: "hello"
   });

■ Documentation
For more details, see:
- docs/generic-sidecar-plugin.md (Plugin specification)
