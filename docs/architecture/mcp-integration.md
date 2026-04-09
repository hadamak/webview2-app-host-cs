# MCP Integration

## Scope

`McpConnector` and `McpBridge` expose the host runtime as a Model Context Protocol surface for AI clients.

## Modes

- `--mcp-headless`
  - Runs without WebView2 and exposes configured DLLs and sidecars through stdio.
- `--mcp`
  - Runs the browser host and adds MCP alongside it.
- `--mcp-proxy`
  - Connects to the main host through named pipes and forwards MCP traffic.

## Architectural role

MCP is not treated as a side feature. It is another first-class connector on the bus:

- browser actions can be reflected into MCP tools
- DLL and sidecar capabilities can be reused by AI clients
- browser-specific tools are abstracted through `IBrowserTools`

## Testing focus

`tests/HostTests/McpTests.cs` validates protocol behavior and browser-tool wiring. This keeps AI integration under the same regression discipline as the desktop host.
