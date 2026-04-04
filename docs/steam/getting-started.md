# Steam 対応の最短導入手順

既存の HTML / JavaScript ゲームを最短で Steam 対応するためのガイドです。

---

## 0. Steamworks 管理画面での事前設定

使用する機能（実績、統計、リーダーボード、クラウド）は、あらかじめ Steamworks Partner サイトで定義し、**Publish** しておく必要があります。

> 開発中は AppID `480`（Spacewar）を使用することで、自前の設定なしで動作確認が可能です。

---

## 1. DLL の配置

`WebView2AppHost.exe` と同じフォルダに以下の DLL を配置します。

```text
MyGame/
├── WebView2AppHost.exe
├── WebView2AppHost.GenericDllPlugin.dll   ← 汎用 DLL プラグイン
├── Facepunch.Steamworks.Win64.dll         ← Steamworks C# ラッパー
├── steam_api64.dll                        ← Steam 公式 SDK
└── www/
    ├── index.html
    ├── host.js                            ← プラグインブリッジ
    └── app.conf.json
```

---

## 2. `app.conf.json` の設定

`GenericDllPlugin` を有効化し、Steamworks DLL をエイリアス `Steam` としてロードします。

```json
{
  "plugins": ["GenericDllPlugin"],
  "loadDlls": [
    {
      "alias": "Steam",
      "dll": "Facepunch.Steamworks.Win64.dll",
      "exposeEvents": ["OnAchievementProgress", "OnGameOverlayActivated"]
    }
  ],
  "steamAppId": "480",
  "steamDevMode": true
}
```

---

## 3. `host.js` の読み込み

`host.js` を読み込むことで、JavaScript から `Host` オブジェクトを介して全機能へアクセスできるようになります。

```html
<script src="host.js"></script>
```

---

## 4. 最小コード

```html
<script>
async function main() {
    try {
        // Steam プレイヤー名を表示してみる
        // Host.<Alias>.<ClassName>.<MemberName> で呼び出せます
        await Host.Steam.SteamClient.Init(480, true);
        const name = await Host.Steam.SteamClient.Name();
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
await Host.Steam.SteamUserStats.SetAchievement('ACH_WIN_ONE_GAME');
await Host.Steam.SteamUserStats.StoreStats();
```

### スクリーンショットの保存

WebView2 の画面をキャプチャし、Steam のスクリーンショットライブラリに保存します。

```js
// 1. WebView2 の画面を画像ファイルとして出力
const preview = await Host.Internal.WebView.CapturePreview("screenshot.png");

// 2. 出力されたファイルを Steam に登録
await Host.Steam.SteamScreenshots.AddScreenshot(
    preview.path, "", preview.width, preview.height
);
```

### コールバックイベントの受信

```js
Host.on('OnAchievementProgress', ({ achievementName, currentProgress, maxProgress }) => {
    console.log(`Progress: ${achievementName} ${currentProgress}/${maxProgress}`);
});
```

---

## 6. 呼び出し可能な API と制限事項

Facepunch.Steamworks の**静的 API** をほぼそのまま呼び出せます。詳細は [Facepunch.Steamworks ドキュメント](https://wiki.facepunch.com/steamworks/) を参照してください。

### ⚠️ 重要な制限事項（データサイズ）

- **通信サイズ制限**: ホストと JavaScript 間の通信（JSON シリアライザ）にはサイズ制限（約 2MB）があります。
- **バイナリ転送の回避**: `byte[]` などのバイナリデータは JSON 上で数値配列（`[255, 0, ...]`）として転送されるため、メモリ効率が極めて悪くなります。
- **巨大なデータの扱い**: スクリーンショットの画像データ、音声データ、巨大なセーブデータなどを**数値配列として直接やり取りすることはできません。**
- **推奨される設計**: 巨大なデータはファイルとして書き出してからパスを渡す（スクリーンショットの例のように）、あるいはプラグイン側（C#）で処理を完結させるようにしてください。
