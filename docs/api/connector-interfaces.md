# Connector Interfaces

## `IConnector`

The common connector contract is intentionally small:

- `Name`
- `Publish`
- `Deliver(string messageJson)`
- `Dispose()`

This keeps routing in the bus and keeps transport-specific behavior inside each connector.

## `IBrowserTools`

The browser abstraction used by MCP exposes:

- `EvaluateAsync`
- `ScreenshotAsync`
- `NavigateAsync`
- `GetUrlAsync`
- `GetContentAsync`

This lets AI tooling depend on browser capabilities without depending directly on WebView2 types.
