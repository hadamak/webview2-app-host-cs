# Steam 対応の最短導入手順

Steam 連携は structured `app.conf.json` で構成します。旧 `plugins` / `loadDlls` / `steamAppId` 形式は使いません。

## 配置

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

詳細は [docs/guides/steam-integration.md](../guides/steam-integration.md) を参照してください。
