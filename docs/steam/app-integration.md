# Steam サポート利用ガイド

この文書は、ビルド済みの Steam サポート ZIP を使って Web コンテンツへ Steamworks 連携を追加するアプリ開発者向けです。

この文脈では Steamworks SDK のダウンロードは不要です。必要なのは通常版の本体配布物と、別途ローカルで作成された Steam サポート ZIP だけです。

---

## 1. 何を受け取ればよいか

通常のアプリ開発者が必要なのは次の 2 つです。

- 通常版の本体配布物
- Steam サポート ZIP

Steam サポート ZIP に含まれる想定の主な内容:

- `steam_bridge.dll`
- `steam_api64.dll`
- `steam.js`
- `steam-sample/`
- `STEAM.md`

---

## 2. Steam 対応の考え方

Steam 連携は `steam.js` を介した独自 API です。本体ホストが標準 Web API 中心なのに対し、Steam 機能だけは追加レイヤーとして扱います。

重要:

- WebView2 の制約により、Steam オーバーレイはゲーム画面上に重なって表示されません
- 実績や関連 UI は Steam アプリケーション側の UI / ウィンドウとして扱われます
- `showOverlay*` 系 API は Steamworks の命名をそのまま使っていますが、「Steam 側 UI を開く API」と理解してください

---

## 3. 必要ファイル

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

開発時のみ、必要に応じて `steam_appid.txt` を EXE と同じ場所に置けます。

---

## 4. `app.conf.json`

```json
{
  "title": "My Game",
  "width": 1280,
  "height": 720,
  "steamAppId": "480",
  "steamDevMode": true
}
```

| キー | 型 | 説明 |
|---|---|---|
| `steamAppId` | string | Steam AppID |
| `steamDevMode` | bool | `true` は開発向け、`false` は Steam 起動前提のリリース向け |

---

## 5. `steam.js` の導入

```html
<script src="steam.js"></script>
```

ブラウザで直接開いた場合でも落ちず、`Steam.isAvailable()` が `false` になるだけです。

---

## 6. 最小コード例

```html
<script src="steam.js"></script>
<script>
async function main() {
    const info = await Steam.init();

    if (!info.isAvailable) {
        console.log('Steam is unavailable');
        return;
    }

    console.log(info.personaName);
    await Steam.unlockAchievement('ACHIEVEMENT_1');
    Steam.showOverlay('achievements');
}

main();
</script>
```

`Steam.init()` の成否を確認してから他の API を使ってください。

---

## 7. 主な API

### 初期化

- `Steam.isAvailable()`
- `await Steam.init()`

### 実績

- `await Steam.unlockAchievement(name)`
- `await Steam.clearAchievement(name)`

### Steam UI 呼び出し

- `Steam.showOverlay(option)`
- `Steam.showOverlayURL(url, modal)`
- `Steam.showOverlayInviteDialog(lobbyId)`

### DLC

- `await Steam.checkDlcInstalled([appId1, appId2])`
- `Steam.installDlc(appId)`
- `Steam.uninstallDlc(appId)`

### リッチプレゼンス

- `Steam.setRichPresence(key, value)`
- `Steam.clearRichPresence()`

### スクリーンショット

- `Steam.triggerScreenshot()`

### 認証

- `await Steam.getAuthTicketForWebApi(identity)`
- `Steam.cancelAuthTicket(authTicket)`

---

## 8. 完全サンプル

完全サンプルは `samples/steam-complete/` にあります。Steam サポート ZIP にも `steam-sample/` として含める想定です。

確認できる内容:

- `Steam.init()`
- ユーザー情報表示
- Steam UI 呼び出し
- 実績解除 / クリア
- DLC 状態確認
- リッチプレゼンス
- スクリーンショット
- Web API チケット取得

---

## 9. トラブルシューティング

### `Steam.init()` が `isAvailable: false`

- `steam_bridge.dll` が EXE と同じ場所にあるか
- `steam_api64.dll` が EXE と同じ場所にあるか
- Steam クライアントが起動しているか
- AppID が正しいか

### Steam UI が期待どおりに見えない

- WebView2 上に重なるオーバーレイではないことを前提にしているか
- Steam クライアント経由で起動しているか
- リリース時に `steamDevMode: false` になっているか
