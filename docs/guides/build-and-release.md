# Build And Release

## Build configurations

### `Debug`

- intended for local development
- copies `test-www\` into output `www\`
- useful when editing loose content without rebuilding the embedded resource path every time

### `Release`

- standard shipping configuration
- includes browser, sidecar, pipe, MCP, and CDP-capable code paths

### `SecureRelease`

- restricted offline-oriented configuration
- defines `SECURE_OFFLINE`
- excludes:
  - `McpConnector`
  - `McpBridge`
  - `SidecarConnector`
  - pipe connectors
  - `CdpProxyHandler`

## Runtime modes

- normal desktop mode
  - `WebView2AppHost.exe`
- desktop + MCP
  - `WebView2AppHost.exe --mcp`
- headless MCP server
  - `WebView2AppHost.exe --mcp-headless`
- MCP proxy
  - `WebView2AppHost.exe --mcp-proxy`

`SecureRelease` rejects all MCP-related modes.

## Local setup helper

`tools/setup-local-dev.ps1`:

- builds the host
- copies Facepunch Steamworks outputs when available
- copies the Python sidecar sample into the build output

Examples:

```powershell
.\tools\setup-local-dev.ps1
.\tools\setup-local-dev.ps1 -Configuration Release
```

## Release packaging helper

`tools/package-release.ps1`:

- builds `Release`
- stages files under `dist\_build\WebView2AppHost-win-x64`
- creates `dist\WebView2AppHost-win-x64.zip`
- creates `dist\WebView2AppHost-win-x64.zip.sha256`

## Included files in the packaged ZIP

- host executable and config
- required WebView2 runtime DLLs used by the host package
- default `www\`
- `README.md`, `README.ja.md`
- `LICENSE`, `THIRD_PARTY_NOTICES.md`
- `docs\` except `docs\maintainer\`
- `samples\`

## Not included

- Node.js or Python runtimes
- third-party Steam binaries
- application-specific DLLs or sidecar executables beyond the sample files
