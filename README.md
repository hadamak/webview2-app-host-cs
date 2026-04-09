# WebView2AppHost

[![Build](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml/badge.svg)](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/hadamak/webview2-app-host-cs)](https://github.com/hadamak/webview2-app-host-cs/releases/latest)
[![License: MIT](https://img.shields.io/github/license/hadamak/webview2-app-host-cs)](LICENSE)

WebView2AppHost is a small Windows host for shipping HTML/CSS/JavaScript apps as desktop applications with WebView2.

It is useful in two very different ways:

- as an adopter, you can package and ship a web app with little or no host-side coding
- as a maintainer, you can extend the host with connectors, AI tooling, and low-level browser integration

This README is for adopters first.

> Japanese README: [README.ja.md](README.ja.md)

## Why Use It

- No heavyweight Chromium bundle
  - WebView2 runtime is already present on many Windows systems
- No backend required for the basic case
  - you can ship static web content without writing C#, Node.js, or Python
- Optional local extensions without rebuilding the host
  - DLLs and external executables can be placed next to the app and enabled through configuration
- No build environment required for content-only distribution
  - a prebuilt EXE can host `www\`, a ZIP, embedded content, DLLs, and sidecars
- Standard web platform behavior
  - `window.close()`, fullscreen APIs, links, dialogs, clipboard, media, and most browser-side code work as expected
- Optional power features when you need them
  - DLLs, sidecars, named pipes, MCP, and CDP are available, but not required to get started

## Fastest Path

If you want to wrap a web app as a desktop EXE:

1. Get a built `WebView2AppHost.exe`
2. Put your web files in `www\` next to the EXE
3. Add `www\app.conf.json`
4. Run the EXE

That is enough for the common case. If your app needs local extensions, you can also place DLLs or external executables next to the host and enable them with configuration. You still do not need to rebuild the host itself.

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
  "url": "https://app.local/index.html"
}
```

## Zero-Rebuild Extension Model

The host is useful not only for static content. A prebuilt package can also light up local capabilities by configuration:

- ship HTML/CSS/JavaScript only
- add a .NET DLL next to the EXE and call it from JavaScript
- add a Node.js, Python, PowerShell, or custom executable and use it as a sidecar
- keep all of that out of the host build as long as the host already supports the needed connector type

Typical deployment shape:

```text
WebView2AppHost.exe
www/
  index.html
  app.conf.json
plugins/
  SystemMonitor.dll
tools/
  agent.js
```

Example:

```json
{
  "title": "My App",
  "window": { "width": 1280, "height": 720, "frame": true },
  "url": "https://app.local/index.html",
  "connectors": [
    { "type": "dll", "path": "plugins/SystemMonitor.dll" },
    { "type": "sidecar", "runtime": "node", "script": "tools/agent.js" }
  ]
}
```

This is the key distinction: the host can stay prebuilt while the app gains local code capabilities through colocated files and configuration.

## Content Placement Options

The host can load content from several places. For adopters, the main options are:

- `www\` next to the EXE
  - easiest for development and content-only deployment
- ZIP passed on the command line
  - useful when the EXE is a reusable shell
- ZIP with the same basename as the EXE
  - useful when you want content separate from the executable
- ZIP appended to the EXE
  - useful for a single-file artifact
- embedded `app.zip`
  - useful when building your own host binary

Priority is:

1. `www\`
2. command-line ZIP
3. sibling ZIP
4. appended ZIP
5. embedded `app.zip`

See [docs/guides/content-packaging.md](docs/guides/content-packaging.md).

## Build And Release

If you are only changing web content, you can often skip rebuilding the host.

When you do need to build the host:

```powershell
msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64
```

Build configurations:

- `Debug`
  - local development, copies `test-www\` into output `www\`
- `Release`
  - standard shipping build
- `SecureRelease`
  - restricted build that removes MCP, sidecar, pipe, and CDP features

Release packaging details are in [docs/guides/build-and-release.md](docs/guides/build-and-release.md).

## What Ships In The Release ZIP

`tools/package-release.ps1` creates a ZIP that includes:

- the host EXE and config
- required WebView2 DLLs for the packaged host
- default `www\`
- `README.md`, `README.ja.md`
- `LICENSE`, `THIRD_PARTY_NOTICES.md`
- `docs\` except maintainer-only docs
- `samples\`

It does not include optional third-party runtimes or integrations such as Node.js, Python, or Steam binaries.

## Optional Advanced Features

You only need these if your app needs them, and in many cases they can be enabled without rebuilding the host:

- `DllConnector`
  - place a .NET DLL next to the app and call it from JavaScript
- `SidecarConnector`
  - place a Node.js, Python, PowerShell, or other executable/script next to the app and talk to it
- pipe connectors
  - integrate with other Windows processes
- MCP
  - expose local tools and content to AI clients

Guides:

- [docs/guides/generic-dll-plugin.md](docs/guides/generic-dll-plugin.md)
- [docs/guides/generic-sidecar-plugin.md](docs/guides/generic-sidecar-plugin.md)
- [docs/guides/steam-integration.md](docs/guides/steam-integration.md)

## Configuration

`app.conf.json` is structured-only.

```json
{
  "title": "My App",
  "window": { "width": 1280, "height": 720, "frame": true },
  "url": "https://app.local/index.html",
  "proxy_origins": ["https://api.github.com"],
  "steam": { "app_id": "480", "dev_mode": true },
  "navigation_policy": {
    "external_navigation_mode": "rules",
    "open_in_host": ["*.github.com"],
    "block_request_patterns": ["*ads*"]
  }
}
```

Reference:

- [docs/api/app-conf-json.md](docs/api/app-conf-json.md)

## Samples

- `samples/sidecar-node`
- `samples/sidecar-python`
- `samples/sidecar-powershell`
- `samples/steam-complete`

## For Maintainers

Internal design, compatibility notes, and implementation roadmap are intentionally separated from the adopter guide.

Start here:

- [docs/maintainer/README.md](docs/maintainer/README.md)

## License

- [LICENSE](LICENSE)
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
