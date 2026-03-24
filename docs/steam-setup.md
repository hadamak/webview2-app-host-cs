# Steam 対応セットアップガイド

## 概要

`steam_bridge.dll` を追加することで、WebView2AppHost 上で動作する HTML ゲームから
Steamworks API（実績・オーバーレイ・DLC 等）を利用できます。

DLL が存在しない場合はホストは通常どおり動作するため、
Steam 非対応環境（ブラウザでの開発・テスト）でも動作します。

---

## 1. Steamworks SDK の取得

1. [Steamworks パートナーページ](https://partner.steamgames.com/doc/sdk) から SDK をダウンロード
2. ZIP を展開し、`sdk/` フォルダの中身を以下のパスに配置:

```
src/steam-bridge/steamworks-sdk/
├── public/
│   └── steam/
│       └── steam_api.h   ← このファイルが存在すれば OK
└── redistributable_bin/
    └── win64/
        ├── steam_api64.dll
        └── steam_api64.lib
```

---

## 2. steam_bridge.dll のビルド

Visual Studio 2026 が必要です（Community エディションで可）。

```bat
cd src\steam-bridge
msbuild SteamBridge.vcxproj /p:Configuration=Release /p:Platform=x64
```

ビルド成功後、以下のファイルが自動的に出力されます:

```
src/bin/x64/Release/net472/steam_bridge.dll
```

---

## 3. 配布物の構成

```
MyGame/
├── WebView2AppHost.exe
├── WebView2AppHost.exe.config
├── Microsoft.Web.WebView2.Core.dll
├── Microsoft.Web.WebView2.WinForms.dll
├── WebView2Loader.dll
├── steam_bridge.dll              ← ビルド成果物
├── steam_api64.dll               ← Steamworks SDK から
├── steam_appid.txt               ← 開発時のみ（AppID を記載）
└── game.zip
    ├── index.html
    ├── steam.js                  ← web-content/steam.js をコピー
    └── app.conf.json             ← steamAppId を追記
```

---

## 4. app.conf.json の設定

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
| `steamAppId` | string | Steam AppID。未設定の場合は `steam_appid.txt` に委ねる |
| `steamDevMode` | bool | `true`: 環境変数経由で AppID を渡す（開発向け）。`false`: `SteamAPI_RestartAppIfNecessary()` を実行（リリース向け） |

---

## 5. ゲーム側の実装

```html
<!-- index.html -->
<script src="steam.js"></script>
<script>
// 初期化
const info = await Steam.init();
if (info.isAvailable) {
    console.log('プレイヤー:', info.personaName);
    console.log('AppID:', info.appId);
    console.log('Steam Deck:', info.isRunningOnSteamDeck);
}

// 実績解除
await Steam.unlockAchievement('FIRST_CLEAR');

// オーバーレイ
Steam.showOverlay('achievements');

// イベント
Steam.on('on-game-overlay-activated', ({ isShowing }) => {
    if (isShowing) pauseGame();
    else           resumeGame();
});
</script>
```

---

## 6. 開発中のテスト

1. Steam クライアントを起動してログイン
2. `steam_appid.txt` に AppID（例: `480`）を記載して EXE と同じフォルダに置く
3. EXE を起動（Steam クライアント外からでも `steamDevMode: true` なら動作）

テスト用 AppID `480` は Valve の SpaceWar サンプルで、誰でも使用可能です。

---

## トラブルシューティング

| 症状 | 確認箇所 |
|---|---|
| `steam_bridge.dll が見つかりません` のログが出る | ビルド後の DLL が EXE と同じフォルダにあるか確認 |
| `Steam API failed to initialize` のログが出る | Steam クライアントが起動しているか / AppID が正しいか確認 |
| 実績が解除されない | `steamDevMode: false` の場合は Steam から起動しているか確認 |
| `init()` の `isAvailable` が false | 上記 2 点を確認。ブラウザで開いている場合は仕様 |
