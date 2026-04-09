# Generic Sidecar Plugin

Use `SidecarConnector` when you want runtime isolation or need a language outside .NET.

## Typical fit

- Node.js ecosystem packages
- Python data and ML tooling
- PowerShell automation
- crash containment for risky integrations

## Structured config

```json
{
  "connectors": [
    { "type": "sidecar", "runtime": "node", "script": "agent.js" }
  ]
}
```

Legacy `sidecars` remains supported.

## Protocol

- transport: stdio
- framing: newline-delimited JSON
- RPC: JSON-RPC 2.0
- optional readiness handshake: `{ "ready": true }`

## Guidance

- prefer UTF-8
- keep sidecars stateless where possible
- log operational detail to `stderr`, not `stdout`
- treat the sidecar boundary as an API boundary, not just a shell-out
