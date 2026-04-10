# WebView2AppHost

[![Build](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml/badge.svg)](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/hadamak/webview2-app-host-cs)](https://github.com/hadamak/webview2-app-host-cs/releases/latest)
[![License: MIT](https://img.shields.io/github/license/hadamak/webview2-app-host-cs)](LICENSE)

WebView2AppHost is a lightweight Windows desktop host for shipping HTML/CSS/JavaScript applications using WebView2.

It acts as a **transparent host**—meaning standard Web APIs just work. You don't have to learn custom wrapper APIs to manage windows, permissions, dialogs, or media.

It is designed for two primary audiences:
- **Adopters**: Package and ship web apps as desktop executables instantly, with zero host-side coding.
- **Maintainers & Power Users**: Extend capabilities dynamically using a MessageBus-centric connector architecture (Native DLLs, Sidecar processes, and AI/MCP integration).

> Japanese README: [README.ja.md](README.ja.md)

## Why Use It?

- **100% Standard Web APIs**
  - `window.close()`, `requestFullscreen()`, `beforeunload`, `navigator.permissions`, and clipboard work exactly as they do in a standard browser.
- **Zero-Rebuild Extension Model**
  - Need native capabilities? Drop a .NET DLL or a Node.js/Python/PowerShell script next to the EXE and enable it in `app.conf.json`. No need to recompile the host application.
- **Built-in AI (MCP) Integration**
  - First-class Model Context Protocol (MCP) support. Expose your app's state, UI tools, or native capabilities to AI agents seamlessly (supports headless and proxy modes).
- **Transparent CORS Proxy**
  - A built-in Chrome DevTools Protocol (CDP) proxy allows your local `https://app.local` frontend to `fetch()` external APIs without CORS errors.
- **Ultra Lightweight & Portable**
  - Built on .NET 4.8 (pre-installed on modern Windows). No need to bundle a 100MB+ Chromium runtime.

## Fastest Path

If you simply want to wrap a web app as a desktop EXE:

1. Get a prebuilt `WebView2AppHost.exe`
2. Create a `www\` folder next to the EXE and put your web files in it.
3. Add a `www\app.conf.json` file.
4. Run the EXE.

That's it. It runs as a fully functional Windows desktop application.

## Basic App Layout

```text
WebView2AppHost.exe
WebView2AppHost.exe.config
Microsoft.Web.WebView2.Core.dll
Microsoft.Web.WebView2.WinForms.dll
WebView2Loader.dll
www/
  index.html
  app.conf.json
  assets/
  scripts/
```

Minimal `app.conf.json`:

```json
{
  "title": "My App",
  "window": { "width": 1280, "height": 720, "frame": true },
  "url": "https://app.local/index.html",
  "connectors": [
    { "type": "browser" }
  ]
}
```

> **Tips:** If end-users want to override the window size or fullscreen settings locally without modifying the core app config, they can simply place a `user.conf.json` next to the EXE.

## Zero-Rebuild Extension Model

The real power of WebView2AppHost is its extensibility without recompilation. If your app needs system-level capabilities, you just add **Connectors**.

- **`dll` Connector**: Load a .NET DLL placed next to the EXE and call it directly from JavaScript (Perfect for Steamworks integrations).
- **`sidecar` Connector**: Spawn a Node.js, Python, PowerShell, or custom executable as a child process and communicate via JSON-RPC.
- **`pipe_server` / `mcp` Connector**: Allow local automation tools or AI agents (MCP clients) to connect to your app.

**Extended Configuration Example:**

```json
{
  "title": "My Extended App",
  "window": { "width": 1280, "height": 720 },
  "url": "https://app.local/index.html",
  "connectors": [
    { "type": "browser" },
    { "type": "dll", "alias": "Steam", "path": "Facepunch.Steamworks.Win64.dll" },
    { "type": "sidecar", "alias": "PythonRuntime", "runtime": "python", "script": "tools/agent.py", "wait_for_ready": true }
  ]
}
```

**None of this requires rebuilding the host EXE.**

## Content Placement Options

The host can load web content from multiple sources based on your distribution needs. Priority is as follows:

1. **`www\` next to the EXE**: Best for rapid development and straightforward loose-file distribution.
2. **Command-line ZIP**: Run `WebView2AppHost.exe content.zip`. Great when using the EXE as a generic shell.
3. **Sibling ZIP**: `WebView2AppHost.zip`. Good for separating the runtime executable from updatable content.
4. **Appended ZIP**: `copy /b EXE + ZIP`. Perfect for single-file, portable distribution.
5. **Embedded `app.zip`**: Best for a guaranteed fallback when building your own custom host binary.

See [docs/guides/content-packaging.md](docs/guides/content-packaging.md).

## Build And Release

If you need to build the host from source (e.g., to change the application icon or bake an embedded ZIP):

```powershell
msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64
```

Build Configurations:
- `Debug`: For local development. Copies `test-www\` to the output directory.
- `Release`: Standard shipping build.
- `SecureRelease`: A restricted build that permanently strips out MCP, Sidecar, Pipe, and CDP code paths for high-security, offline-only applications.

Use the provided script to generate a clean distribution ZIP:
```powershell
.\tools\package-release.ps1
```

## Samples

Check out the `samples/` directory to see extensions in action immediately:

- `samples/sidecar-node`
- `samples/sidecar-python`
- `samples/sidecar-powershell`
- `samples/steam-complete` (Native DLL integration using Facepunch.Steamworks)

## For Maintainers & Developers

Details regarding internal architecture, API compatibility, and the MessageBus bridge design are kept separate for developers:

- [docs/maintainer/README.md](docs/maintainer/README.md)
- [Web API Compatibility](docs/maintainer/api-compatibility.md)
- [Bridge & Connector Architecture](docs/architecture/bridge-design.md)

## License

- [LICENSE](LICENSE)
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)