# Quick Start

This guide is for developers who want the shortest path from an HTML / JavaScript game to a Steam-ready build.

## What you need

- The standard package
- The Steam support ZIP

Normal app developers do not need to download the Steamworks SDK.

## Layout

```text
MyGame/
├── WebView2AppHost.exe
├── WebView2AppHost.exe.config
├── Microsoft.Web.WebView2.Core.dll
├── Microsoft.Web.WebView2.WinForms.dll
├── WebView2Loader.dll
├── steam_bridge.dll
├── steam_api64.dll
└── www/ or game.zip
    ├── index.html
    ├── steam.js
    └── app.conf.json
```

## `app.conf.json`

```json
{
  "title": "My Game",
  "width": 1280,
  "height": 720,
  "steamAppId": "480",
  "steamDevMode": true
}
```

## Load `steam.js`

```html
<script src="steam.js"></script>
```

## Minimal code

```html
<script src="steam.js"></script>
<script>
async function main() {
    const steam = await Steam.init();

    if (!steam.isAvailable) {
        console.log('Steam is unavailable');
        return;
    }

    console.log('Player:', steam.personaName);
}

main();
</script>
```

## Quick examples

### Achievement

```js
await Steam.unlockAchievement('ACH_WIN_ONE_GAME');
```

### User stats

```js
await Steam.setStatInt('NumGames', 10);
await Steam.storeStats();
```

### Steam Cloud

```js
await Steam.writeCloudFileText('save.json', JSON.stringify({ level: 3 }));
```

### Leaderboards

```js
const board = await Steam.findOrCreateLeaderboard('Feet Traveled', 'descending', 'numeric');
await Steam.uploadLeaderboardScore(board.leaderboardHandle, 5000);
```

## AppID `480` note

The bundled sample uses AppID `480`, which is Valve's Spacewar test app.  
That means the achievement names, stat names, and leaderboard names must match Spacewar's predefined names.

Examples:

- Achievement: `ACH_WIN_ONE_GAME`
- Int stat: `NumGames`
- Float stat: `FeetTraveled`
- Leaderboard: `Feet Traveled`

When you switch to your own title, replace these with your own Steamworks API names.

## Next documents

- Feature guides: `docs/steam/en/feature-guides/`
- API reference: `docs/steam/en/api-reference.md`
