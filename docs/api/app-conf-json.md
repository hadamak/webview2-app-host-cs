# app.conf.json

`app.conf.json` is a structured schema. Legacy flat connector arrays and camelCase aliases are no longer accepted.

## Full example

```json
{
  "title": "My App",
  "window": {
    "width": 1280,
    "height": 720,
    "frame": true,
    "fullscreen": false
  },
  "url": "https://app.local/index.html",
  "proxy_origins": ["https://api.github.com"],
  "steam": {
    "app_id": "480",
    "dev_mode": true
  },
  "navigation_policy": {
    "external_navigation_mode": "rules",
    "open_in_host": ["*.github.com"],
    "open_in_browser": ["login.microsoftonline.com"],
    "block": ["ads.example.com"],
    "allowed_external_schemes": ["https", "mailto"],
    "block_request_patterns": ["*ads*"]
  },
  "connectors": [
    {
      "type": "browser",
      "alias": "Browser"
    },
    {
      "type": "dll",
      "alias": "SystemMonitor",
      "path": "plugins/SystemMonitor.dll",
      "expose_events": ["OnStatusChanged"]
    },
    {
      "type": "sidecar",
      "alias": "PythonRuntime",
      "runtime": "python",
      "script": "python-runtime/server.py",
      "working_directory": "python-runtime",
      "wait_for_ready": true,
      "encoding": "utf-8"
    },
    {
      "type": "pipe_server"
    },
    {
      "type": "mcp"
    }
  ]
}
```

## Top-level fields

- `title`
  - Window title used before the page updates `document.title`.
- `window`
  - `width`, `height`, `frame`, `fullscreen`
- `url`
  - Initial browser URL. Default is `https://app.local/index.html`.
- `proxy_origins`
  - Origins that CDP proxying may intercept in standard builds.
- `steam`
  - `app_id`, `dev_mode`
- `navigation_policy`
  - External navigation routing and request blocking.
- `connectors`
  - List of enabled bridge surfaces.

## Connector types

- `browser`
  - Registers `BrowserConnector`.
- `dll`
  - Fields: `type`, `path`, optional `alias`, optional `expose_events`
- `sidecar`
  - Fields: `type`, `runtime` or `executable`, optional `script`, optional `alias`, optional `working_directory`, optional `mode`, optional `args`, optional `encoding`, optional `wait_for_ready`
- `pipe` / `pipe_server`
  - Registers `PipeServerConnector`.
- `mcp`
  - Enables `McpConnector` in supported builds.

## Navigation policy

```json
{
  "external_navigation_mode": "rules",
  "open_in_host": ["*.github.com"],
  "open_in_browser": ["example.com"],
  "block": ["blocked.example.com"],
  "allowed_external_schemes": ["https", "mailto"],
  "block_request_patterns": ["*ads*"]
}
```

- `external_navigation_mode`
  - `host`, `browser`, `rules`, or `block`
- `open_in_host`
  - Used by `rules` to keep matching `http(s)` URLs inside the host
- `open_in_browser`
  - Used by `rules` to send matching `http(s)` URLs to the default browser
- `block`
  - Host deny list applied before other `http(s)` routing rules
- `allowed_external_schemes`
  - Scheme allow-list checked before host rules
- `block_request_patterns`
  - Request-level filter separate from top-level navigation routing

## Notes

- `connectors` is now the only supported way to declare DLLs and sidecars.
- **Selective Activation**: If the `connectors` array is present, the host will **only** start the connectors explicitly listed. For example, if you want to allow external MCP clients to attach via `--mcp-proxy`, you must include `{ "type": "pipe_server" }` in the list.
- Use snake_case field names in `app.conf.json`.
- `user.conf.json` may still override `width`, `height`, and `fullscreen` at runtime.
