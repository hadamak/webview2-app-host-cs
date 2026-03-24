# Steam API Reference

This is the function reference for `steam.js`.  
If you want to understand the features first, read `docs/steam/en/overview.md` and the feature guides before this page.

## Initialization

- `Steam.isAvailable()`
- `await Steam.init()`

## Achievements

- `await Steam.unlockAchievement(name)`
- `await Steam.clearAchievement(name)`
- `await Steam.getAchievementState(name)`

Common failure:

- The achievement API name does not match the Steamworks definition

## User Stats

- `await Steam.getStatInt(name)`
- `await Steam.getStatFloat(name)`
- `await Steam.setStatInt(name, value)`
- `await Steam.setStatFloat(name, value)`
- `await Steam.storeStats()`

On failure, `reason` may be `stat-not-found-or-type-mismatch`.

## Steam UI

- `Steam.showOverlay(option)`
- `Steam.showOverlayURL(url, modal)`
- `Steam.showOverlayInviteDialog(lobbyId)`

These open Steam-side UI. They do not draw an overlay inside the WebView2 surface.

## Steam Cloud

- `await Steam.getCloudStatus()`
- `await Steam.listCloudFiles()`
- `await Steam.cloudFileExists(fileName)`
- `await Steam.readCloudFile(fileName)`
- `await Steam.readCloudFileText(fileName)`
- `await Steam.writeCloudFile(fileName, dataBase64)`
- `await Steam.writeCloudFileText(fileName, text)`
- `await Steam.deleteCloudFile(fileName)`

## Leaderboards

- `await Steam.findLeaderboard(name)`
- `await Steam.findOrCreateLeaderboard(name, sortMethod, displayType)`
- `await Steam.uploadLeaderboardScore(leaderboardHandle, score, options)`
- `await Steam.downloadLeaderboardEntries(leaderboardHandle, requestType, rangeStart, rangeEnd)`

## Ownership / DLC

- `await Steam.getAppOwnershipInfo()`
- `await Steam.isSubscribedApp(appId)`
- `await Steam.checkDlcInstalled([appId1, appId2])`
- `await Steam.getDlcList()`
- `Steam.installDlc(appId)`
- `Steam.uninstallDlc(appId)`

## Rich Presence

- `Steam.setRichPresence(key, value)`
- `Steam.clearRichPresence()`

## Auth

- `await Steam.getAuthTicketForWebApi(identity)`
- `Steam.cancelAuthTicket(authTicket)`

## Screenshots

- `Steam.triggerScreenshot()`

## Events

- `Steam.on('on-game-overlay-activated', handler)`
- `Steam.on('on-dlc-installed', handler)`
- `Steam.on('screenshot-requested', handler)`
