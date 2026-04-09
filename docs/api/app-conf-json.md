# app.conf.json

## Supported styles

The loader accepts both:

- legacy flat fields such as `title`, `width`, `height`, `fullscreen`, `proxyOrigins`, `loadDlls`, and `sidecars`
- structured fields such as `window`, `url`, `navigation_policy`, `sub_streams`, and `connectors`

Structured entries are normalized into the current runtime model, so older connector initialization code keeps working.

## Structured fields

### `window`

```json
{ "width": 1280, "height": 720, "frame": true, "fullscreen": false }
```

### `url`

Initial browser URL. Defaults to `https://app.local/index.html`.

### `navigation_policy`

```json
{
  "external_navigation_mode": "system_browser",
  "allow_external_hosts": ["*.github.com"],
  "block_external_hosts": ["ads.example.com"],
  "allowed_external_schemes": ["https", "mailto"],
  "block_request_patterns": ["*ads*"]
}
```

Current runtime status:

- external navigation routing now uses the configured mode and lists
- `block_request_patterns` remains independent from top-level navigation routing
- design rationale is documented in [../maintainer/navigation-policy-redesign.md](../maintainer/navigation-policy-redesign.md)

Planned meanings:

- `external_navigation_mode`
  - `system_browser`, `whitelist`, or `block`
- `allow_external_hosts`
  - host whitelist used by `whitelist`
- `block_external_hosts`
  - host deny list used by `system_browser`
- `allowed_external_schemes`
  - explicit scheme allow list enforced before host matching
- `block_request_patterns`
  - request-level filtering, independent from top-level navigation routing

### `sub_streams`

```json
{ "enabled": true, "max_concurrent_streams": 5 }
```

### `connectors`

```json
[
  { "type": "dll", "path": "plugins/SystemMonitor.dll" },
  { "type": "sidecar", "runtime": "python", "script": "agent.py" }
]
```

Supported connector shapes:

- DLL
  - `type`, `path`, optional `alias`, optional `expose_events`
- sidecar
  - `type`, `runtime` or `executable`, optional `script`, `working_directory`, `mode`, `args`, `encoding`, `wait_for_ready`

## Legacy fields still supported

### Window and title

- `title`
- `width`
- `height`
- `fullscreen`

### Browser proxy

- `proxyOrigins`

### Steam

- `steamAppId`
- `steamDevMode`

### DLL loading

```json
"loadDlls": [
  { "alias": "Steam", "dll": "Facepunch.Steamworks.Win64.dll", "exposeEvents": ["OnGameOverlayActivated"] }
]
```

### Sidecars

```json
"sidecars": [
  {
    "alias": "Python",
    "mode": "streaming",
    "executable": "python",
    "workingDirectory": "python-runtime",
    "args": ["server.py"],
    "encoding": "utf-8",
    "waitForReady": true
  }
]
```

## User overrides

If `user.conf.json` exists next to the EXE, it can override:

- `width`
- `height`
- `fullscreen`

This applies after `app.conf.json` is loaded.
