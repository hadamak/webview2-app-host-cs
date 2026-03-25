# Steam 対応の最短導入手順

この文書は、「既存の HTML / JavaScript ゲームを最短で Steam 対応したい」人向けです。

---

## 0. Steamworks 管理画面での事前設定（コード・ZIP より先）

**コードを書く前に、Steamworks Partner サイトで設定が必要です。**  
使う機能だけ対応してください。未設定のまま API を呼んでも常に失敗します。

| 使う機能 | 必要な手順 |
|---------|-----------|
| 実績 | App Admin → Achievements → 実績を定義 → **Publish** |
| User Stats | App Admin → Stats → stat 名・型・初期値を定義 → **Publish** |
| Leaderboards | App Admin → Leaderboards → ボードを作成 → **Publish** |
| Steam Cloud | App Admin → Steam Cloud → 有効化・クォータ設定 → **Publish** |

> 開発中は AppID `480`（Valve の Spacewar）を使えば上記設定なしで動作確認できます。  
> Spacewar には実績・stats・leaderboard が既に定義されているためです。  
> 自分のタイトルに切り替えたら必ず上記を設定してください。

---

## 1. 必要なもの

- 通常版の本体配布物
- Steam サポート ZIP

通常のアプリ開発者は Steamworks SDK をダウンロードする必要はありません。

---

## 2. 配置

```text
MyGame/
├── WebView2AppHost.exe
├── WebView2AppHost.exe.config
├── Microsoft.Web.WebView2.Core.dll
├── Microsoft.Web.WebView2.WinForms.dll
├── WebView2Loader.dll
├── steam_bridge.dll
├── steam_api64.dll
└── www/ または game.zip
    ├── index.html
    ├── steam.js
    └── app.conf.json
```

---

## 3. `app.conf.json`

```json
{
  "title": "My Game",
  "width": 1280,
  "height": 720,
  "steamAppId": "480",
  "steamDevMode": true
}
```

- `steamAppId`: Steam の AppID
- `steamDevMode`: 開発時は `true`、Steam 起動前提のリリースでは通常 `false`

---

## 4. `steam.js` を読み込む

```html
<script src="steam.js"></script>
```

---

## 5. 最小コード

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

まずは `Steam.init()` が成功するところまで確認してください。

---

## 6. すぐ使える例

### 実績

```js
await Steam.unlockAchievement('ACH_WIN_ONE_GAME');
```

### User Stats

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

---

## 7. AppID `480` の注意

付属サンプルは開発用に `480` を使います。これは Valve の Spacewar 用 AppID です。  
そのため、実績名や stat 名や leaderboard 名も Spacewar 側で定義済みのものを使う必要があります。

例:

- Achievement: `ACH_WIN_ONE_GAME`
- Int stat: `NumGames`
- Float stat: `FeetTraveled`
- Leaderboard: `Feet Traveled`

自分のタイトルで使うときは、Steamworks 管理画面で登録した API 名に置き換えてください。

---

## 8. 次に読む文書

- 機能の意味から知りたい: `docs/steam/feature-guides/`
- 関数単位で見たい: `docs/steam/api-reference.md`
