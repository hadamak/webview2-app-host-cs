# Steam 対応の最短導入手順

既存の HTML / JavaScript ゲームを最短で Steam 対応したい方向けのガイドです。

---

## 0. Steamworks 管理画面での事前設定（コードより先）

使う機能だけ対応してください。未設定のまま API を呼んでも常に失敗します。

| 使う機能 | 必要な手順 |
|---------|-----------| 
| 実績 | App Admin → Achievements → 実績を定義 → **Publish** |
| User Stats | App Admin → Stats → stat 名・型・初期値を定義 → **Publish** |
| Leaderboards | App Admin → Leaderboards → ボードを作成 → **Publish** |
| Steam Cloud | App Admin → Steam Cloud → 有効化・クォータ設定 → **Publish** |

> 開発中は AppID `480`（Valve の Spacewar）を使えば上記設定なしで動作確認できます。

---

## 1. DLL の配置（これだけで Steam 機能が有効になります）

`WebView2AppHost.exe` と**同じフォルダ**に以下の3ファイルを置くだけです。

```text
MyGame/
├── WebView2AppHost.exe
├── WebView2AppHost.Steam.dll          ← Steam サポート ZIP に同梱
├── Facepunch.Steamworks.Win64.dll     ← Steam サポート ZIP に同梱
├── steam_api64.dll                    ← Steam サポート ZIP に同梱
└── www/ または game.zip
    ├── index.html
    ├── steam.js
    └── app.conf.json
```

`WebView2AppHost.Steam.dll` が存在しない場合、Steam 機能は自動的に無効になります（アプリはクラッシュしません）。

---

## 2. `app.conf.json` の設定

```json
{
  "title": "My Game",
  "width": 1280,
  "height": 720,
  "steamAppId": "480",
  "steamDevMode": true
}
```

- `steamAppId`: Steam の AppID（本番では自分のタイトルの AppID に変更）
- `steamDevMode`: 開発時は `true`

---

## 3. `steam.js` の読み込み

```html
<script src="steam.js"></script>
```

---

## 4. 最小コード

```html
<script src="steam.js"></script>
<script>
async function main() {
    // SteamClient の初期化確認（init メソッドは C# 側で行われるため、
    // JS 側は直接 API を呼び出せるかどうかを確認する）
    try {
        const name = await Steam.SteamFriends.GetPersonaName();
        console.log('Player:', name);
    } catch (e) {
        console.log('Steam is unavailable:', e.message);
    }
}
main();
</script>
```

---

## 5. よく使う例

### 実績の解除

```js
await Steam.SteamUserStats.SetAchievement('ACH_WIN_ONE_GAME');
await Steam.SteamUserStats.StoreStats();
```

### User Stats の設定

```js
await Steam.SteamUserStats.SetStat('NumGames', 10);
await Steam.SteamUserStats.StoreStats();
```

### Leaderboard へのスコア投稿

```js
// 汎用インスタンス・ディスパッチャにより、JS側でシームレスにメソッドを呼べます
const board = await Steam.SteamUserStats.FindOrCreateLeaderboardAsync(
    'Feet Traveled',
    2,   // LeaderboardSort.Descending
    1    // LeaderboardDisplay.Numeric
);
await board.SubmitScoreAsync(5000);
```

### Steam Cloud への書き込み

```js
const data = JSON.stringify({ level: 3, score: 9000 });
const bytes = new TextEncoder().encode(data);
await Steam.SteamRemoteStorage.FileWriteAsync('save.json', bytes, bytes.length);
```

### スクリーンショット

```js
// WebView2 の画面をキャプチャして Steam に登録（Base64 変換なし、C# 側で直接処理）
await Steam.SteamScreenshots.TriggerScreenshot();
```

### コールバックイベントの受信

```js
Steam.on('OnAchievementProgress', ({ achievementName, currentProgress, maxProgress }) => {
    console.log(`Progress: ${achievementName} ${currentProgress}/${maxProgress}`);
});

Steam.on('OnGameOverlayActivated', ({ active }) => {
    document.querySelector('#overlay-indicator').hidden = !active;
});
```

---

## 6. 呼び出し可能な API の調べ方

JavaScript からは Facepunch.Steamworks の**すべての静的 API** をそのまま呼び出せます。

1. [Facepunch.Steamworks ドキュメント](https://wiki.facepunch.com/steamworks/) を開く
2. 使いたいクラス（例: `SteamUserStats`）とメソッド名を確認する
3. `Steam.SteamUserStats.SetAchievement(...)` のように呼び出す

API を追加しても `steam.js` の変更は一切不要です。

---

## 7. AppID `480`（Spacewar）使用時の注意

付属サンプルは開発用に `480` を使います。実績名・stat 名・leaderboard 名も Spacewar のものを使う必要があります。

- Achievement: `ACH_WIN_ONE_GAME`
- Int stat: `NumGames`
- Float stat: `FeetTraveled`
- Leaderboard: `Feet Traveled`

自分のタイトルでは Steamworks 管理画面で登録した API 名に置き換えてください。
