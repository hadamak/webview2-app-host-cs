# Generic DLL Connector

Use `connectors` with `type: "dll"` to expose a colocated .NET assembly without rebuilding the host.

## Example

```json
{
  "connectors": [
    {
      "type": "dll",
      "alias": "Steam",
      "path": "Facepunch.Steamworks.Win64.dll",
      "expose_events": ["OnGameOverlayActivated"]
    }
  ],
  "steam": {
    "app_id": "480",
    "dev_mode": true
  }
}
```

## Fields

- `alias`
  - Bridge name used from JavaScript.
- `path`
  - DLL path relative to the EXE unless absolute.
- `expose_events`
  - .NET events forwarded to JavaScript notifications.

## JavaScript

```js
await Host.Steam.SteamClient.Init(480, true);
const name = await Host.Steam.SteamClient.Name();
```

See also [docs/guides/generic-dll-plugin.md](guides/generic-dll-plugin.md).
