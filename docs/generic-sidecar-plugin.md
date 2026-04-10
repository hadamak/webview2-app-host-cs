# Generic Sidecar Connector

Use `connectors` with `type: "sidecar"` when you need a colocated executable or script outside .NET.

## Example

```json
{
  "connectors": [
    {
      "type": "sidecar",
      "alias": "NodeBackend",
      "runtime": "node",
      "script": "node-runtime/server.js",
      "working_directory": "node-runtime",
      "wait_for_ready": true
    }
  ]
}
```

## Fields

- `runtime` or `executable`
  - Process to launch.
- `script`
  - Prepended to the argument list when using an interpreter runtime.
- `working_directory`
  - Process working directory. Defaults to the EXE directory.
- `mode`
  - `streaming` or `cli`
- `wait_for_ready`
  - Wait for `{ "ready": true }` during startup.

## Protocol

- transport: stdio
- framing: newline-delimited JSON
- RPC: JSON-RPC 2.0

See also [docs/guides/generic-sidecar-plugin.md](guides/generic-sidecar-plugin.md).
