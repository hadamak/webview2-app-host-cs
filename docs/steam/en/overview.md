# Steam Integration Overview

This guide is for browser game developers who want to ship on Steam without learning the Steamworks SDK first.

The intended flow is:

1. Use the standard WebView2AppHost package
2. Add the Steam support ZIP only when you need Steam features
3. Call `steam.js` from your HTML / JavaScript game

Normal app developers do not need to build `steam_bridge.dll`.  
Downloading the Steamworks SDK is only necessary for people who modify the bridge itself.

## What you can do

### Achievements

Unlock Steam achievements when the player reaches a condition.

### User Stats

Store numeric values in Steam, such as total runs, total score, or best time.

### Steam Cloud

Sync save files and settings through Steam.

### Leaderboards

Use Steam rankings for high scores and time attacks.

### Rich Presence

Show the player's current status in the Steam friends list.

### Ownership / DLC checks

Check whether the user owns the base app or a DLC and whether a DLC is installed.

## Design goals

- Steam support should feel like "extract one more ZIP"
- App developers should not need the Steamworks SDK
- Documentation should explain game use cases before raw API names
- The included sample should be enough to verify the setup

## Constraints

- Steam UI does not render as an in-window overlay on top of WebView2
- `showOverlay*` opens Steam-side UI
- Steam Deck is not a supported target. This host depends on Windows WebView2 and does not guarantee SteamOS / Proton behavior

## Next documents

- Quick start: `docs/steam/en/getting-started.md`
- Feature guides: `docs/steam/en/feature-guides/`
- API reference: `docs/steam/en/api-reference.md`
