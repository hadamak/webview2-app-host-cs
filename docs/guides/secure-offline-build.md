# Secure Offline Build (`SecureRelease`)

## Purpose

`SecureRelease` is a restricted build configuration designed for applications that:

- run entirely offline with no external process communication,
- must present the smallest possible attack surface,
- need to pass binary-level audits (no MCP, pipe, or CDP symbols in the final executable).

In this configuration, the host can only load in-process .NET DLLs declared in `app.conf.json`. All other extension mechanisms are compiled out at build time.

---

## What is stripped

The following source files are **excluded from compilation** when `SECURE_OFFLINE` is defined:

| File | Feature removed |
|---|---|
| `src/connectors/McpConnector.cs` | MCP server (AI agent integration) |
| `src/McpBridge.cs` | MCP request/response bridge |
| `src/connectors/SidecarConnector.cs` | Child-process sidecar (Node.js, Python, PowerShell, etc.) |
| `src/connectors/PipeClientConnector.cs` | Named Pipe client (proxy mode) |
| `src/connectors/PipeServerConnector.cs` | Named Pipe server |
| `src/CdpProxyHandler.cs` | Chrome DevTools Protocol transparent CORS proxy |

Additionally, `System.Net.Http` is not referenced, so `HttpClient` is not available in the binary.

---

## What remains available

| Feature | Available |
|---|---|
| WebView2 rendering | ✅ |
| Standard Web APIs (`window.close`, fullscreen, permissions, …) | ✅ |
| Custom scheme (`https://app.local/`) served from ZIP | ✅ |
| In-process DLL connector (`DllConnector`) | ✅ |
| Navigation policy (allow / block / open-external) | ✅ (effective ceiling: `block`) |
| favicon tracking | ✅ |
| `user.conf.json` overrides | ✅ |
| Sidecar processes | ❌ |
| MCP / AI agent integration | ❌ |
| Named Pipe communication | ❌ |
| CDP CORS proxy | ❌ |
| `proxy_origins` config | ❌ (config key is parsed but a warning is logged and the feature is skipped) |

---

## Build

```powershell
msbuild src\WebView2AppHost.csproj `
    "/t:Restore;Build" `
    /p:Configuration=SecureRelease `
    /p:Platform=x64
```

Output: `src\bin\x64\SecureRelease\net48\`

The `SECURE_OFFLINE` preprocessor symbol is defined automatically by the project file when `Configuration=SecureRelease`.

---

## Runtime behaviour differences

### Startup arguments

The following CLI flags are rejected at startup with a `NotSupportedException`:

- `--mcp`
- `--mcp-headless`
- `--mcp-proxy`

### Navigation policy

Regardless of `external_navigation_mode` in `app.conf.json`, the effective ceiling is always `block`. External HTTP/HTTPS navigation is never allowed. `mailto:` and other non-HTTP schemes are also blocked.

### Logging

- Minimum log level is raised to `Warn` (debug and info messages are suppressed).
- File logging (`%LOCALAPPDATA%\<exe>\app.log`) is disabled entirely.
- Sensitive-data logging is always off.

### ConnectorFactory

`BuildWithBrowser` returns only a `MessageBus` (no `McpConnector` return value). Only `Browser` and `Host` connectors are available.

`GetAvailableConnectorNames` never returns `Mcp`, `PipeServer`, or any sidecar alias, even if they appear in `app.conf.json`.

---

## Verifying the build

### Symbol check (automated — `SecureOfflineTests`)

`tests/SecureTests/SecureOfflineTests.cs` includes `SecureBinarySymbolTests`, which loads the compiled EXE via reflection and asserts that none of the following type names are present:

```
WebView2AppHost.McpBridge
WebView2AppHost.McpConnector
WebView2AppHost.SidecarConnector
WebView2AppHost.PipeClientConnector
WebView2AppHost.PipeServerConnector
WebView2AppHost.CdpProxyHandler
```

Run the test:

```powershell
dotnet test tests\SecureTests\SecureTests.csproj -c SecureRelease
```

### Manual spot check

```powershell
# Confirm McpConnector symbol is absent from the binary
$bytes = [System.IO.File]::ReadAllText("src\bin\x64\SecureRelease\net48\WebView2AppHost.exe")
if ($bytes -match "McpConnector") { Write-Error "Symbol found!" } else { Write-Host "OK: symbol absent" }
```

---

## Configuration recommendations

For a `SecureRelease` deployment, a minimal `app.conf.json` looks like:

```json
{
  "title": "My Secure App",
  "window": { "width": 1280, "height": 720 },
  "url": "https://app.local/index.html",
  "navigation_policy": {
    "external_navigation_mode": "block"
  },
  "connectors": [
    { "type": "browser" }
  ]
}
```

If a local DLL is required:

```json
{
  "connectors": [
    { "type": "browser" },
    { "type": "dll", "alias": "MyLib", "path": "MyLib.dll" }
  ]
}
```

Keys such as `proxy_origins`, `sidecar`, `mcp`, and `pipe_server` are safe to leave in the file — they are parsed but silently ignored in a `SecureRelease` build.

---

## CI integration

The `build.yml` GitHub Actions workflow already builds and tests `SecureRelease`:

```yaml
- name: Build SecureRelease
  run: msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=SecureRelease /p:Platform=x64

- name: Run SecureTests (SecureRelease)
  run: dotnet test tests\SecureTests\SecureTests.csproj -c SecureRelease
```

The `release.yml` workflow packages both `Release` and `SecureRelease` artifacts when a `v*` tag is pushed.
