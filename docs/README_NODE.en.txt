WebView2 App Host - Node.js Support
====================================

This package is an extension module to add Node.js sidecar support to WebView2 App Host.
It enables server-side features like file system access and process execution directly 
from your JavaScript code via a local Node.js sidecar process.

■ Installation
1.  Place the following files in the same directory as WebView2AppHost.exe:
    - WebView2AppHost.Node.dll
    - Newtonsoft.Json.dll (*Included in the ExtensionBase package)
    - node-runtime/ folder (including server.js and package.json)

2.  Important: Node.js Runtime (node.exe)
    This package DOES NOT include the Node.js runtime executable (node.exe).
    Download the Windows x64 version of node.exe from https://nodejs.org/ 
    and place it inside the node-runtime/ folder.

    Directory structure after installation:
    WebView2AppHost.exe
    WebView2AppHost.Node.dll
    node-runtime/
    ├── node.exe  <-- Manually download and place here
    ├── server.js
    └── package.json

3.  Configure app.conf.json
    Add "Node" to the "plugins" array in your app.conf.json.
    Example: { "plugins": ["Node"] }

■ Usage (JavaScript)
Refer to the `node-test.html` sample.
Communicates with Node.js sidecar via WebView2.postMessage().

■ Notes
- Communicates via Standard Input/Output (StdIO).
- Messages are exchanged in NDJSON (newline-delimited JSON) format.
- Node.js v20.x or later is recommended.
