# Quick Steam Setup

Steam integration now uses structured `app.conf.json`. Do not use the old `plugins`, `loadDlls`, or `steamAppId` layout.

## Layout

```text
MyGame/
├── WebView2AppHost.exe
├── Facepunch.Steamworks.Win64.dll
├── steam_api64.dll
└── www/
    ├── index.html
    ├── host.js
    └── app.conf.json
```

## app.conf.json

```json
{
  "steam": {
    "app_id": "480",
    "dev_mode": true
  },
  "connectors": [
    {
      "type": "dll",
      "alias": "Steam",
      "path": "Facepunch.Steamworks.Win64.dll",
      "expose_events": ["OnAchievementProgress", "OnGameOverlayActivated"]
    }
  ]
}
```

## JavaScript

```html
<script src="host.js"></script>
<script>
async function main() {
    await Host.Steam.SteamClient.Init(480, true);
    const name = await Host.Steam.SteamClient.Name();
    console.log("Player:", name);
}
main();
</script>
```

See [docs/guides/steam-integration.md](../../guides/steam-integration.md) for the longer guide.
