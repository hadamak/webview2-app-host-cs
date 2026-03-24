# Steam API リファレンス

この文書は `steam.js` の関数一覧です。  
「何ができるか」から知りたい場合は先に `docs/steam/overview.md` と `docs/steam/feature-guides/` を参照してください。

---

## 初期化

- `Steam.isAvailable()`
  - ホスト上で Steam ブリッジが使える前提かを返します
- `await Steam.init()`
  - Steam 初期化と基本情報取得を行います
  - 他の API は基本的にこの後で使います

---

## 実績

- `await Steam.unlockAchievement(name)`
- `await Steam.clearAchievement(name)`
- `await Steam.getAchievementState(name)`

よくある失敗:

- 実績 API 名が Steamworks 側定義と一致していない

---

## User Stats

- `await Steam.getStatInt(name)`
- `await Steam.getStatFloat(name)`
- `await Steam.setStatInt(name, value)`
- `await Steam.setStatFloat(name, value)`
- `await Steam.storeStats()`

失敗時は `reason` に `stat-not-found-or-type-mismatch` が返ることがあります。

よくある失敗:

- stat 名が未定義
- int stat を float API で読んでいる
- float stat を int API で書いている

---

## Steam UI

- `Steam.showOverlay(option)`
- `Steam.showOverlayURL(url, modal)`
- `Steam.showOverlayInviteDialog(lobbyId)`

注意:

- WebView2 の上に重なるオーバーレイではありません
- Steam 側 UI を開きます

---

## Steam Cloud

- `await Steam.getCloudStatus()`
- `await Steam.listCloudFiles()`
- `await Steam.cloudFileExists(fileName)`
- `await Steam.readCloudFile(fileName)`
- `await Steam.readCloudFileText(fileName)`
- `await Steam.writeCloudFile(fileName, dataBase64)`
- `await Steam.writeCloudFileText(fileName, text)`
- `await Steam.deleteCloudFile(fileName)`

用途:

- セーブデータ
- 設定ファイル
- JSON ベースの進行状況

---

## Leaderboards

- `await Steam.findLeaderboard(name)`
- `await Steam.findOrCreateLeaderboard(name, sortMethod, displayType)`
- `await Steam.uploadLeaderboardScore(leaderboardHandle, score, options)`
- `await Steam.downloadLeaderboardEntries(leaderboardHandle, requestType, rangeStart, rangeEnd)`

主な引数:

- `sortMethod`: `'ascending'` / `'descending'`
- `displayType`: `'numeric'` / `'time-seconds'` / `'time-milliseconds'`
- `requestType`: `'global'` / `'global-around-user'` / `'friends'` / `'users'`

よくある失敗:

- leaderboard 名が未定義
- 先に handle を取得していない

---

## 所有権確認 / DLC

- `await Steam.getAppOwnershipInfo()`
- `await Steam.isSubscribedApp(appId)`
- `await Steam.checkDlcInstalled([appId1, appId2])`
- `await Steam.getDlcList()`
- `Steam.installDlc(appId)`
- `Steam.uninstallDlc(appId)`

---

## Rich Presence

- `Steam.setRichPresence(key, value)`
- `Steam.clearRichPresence()`

これは Steam フレンド一覧に表示される現在状況です。

---

## 認証

- `await Steam.getAuthTicketForWebApi(identity)`
- `Steam.cancelAuthTicket(authTicket)`

---

## スクリーンショット

- `Steam.triggerScreenshot()`

---

## イベント

- `Steam.on('on-game-overlay-activated', handler)`
- `Steam.on('on-dlc-installed', handler)`
- `Steam.on('screenshot-requested', handler)`
