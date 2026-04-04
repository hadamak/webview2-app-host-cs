# Steam 連携の概要

WebView2 App Host は、**汎用 DLL プラグイン (GenericDllPlugin)** を利用することで、JavaScript から直接 Steamworks API を呼び出せる環境を提供します。

---

## 仕組み：汎用 DLL パススルー

バックエンドに **[Facepunch.Steamworks](https://wiki.facepunch.com/steamworks/)** を採用しています。
`GenericDllPlugin` が JavaScript からのメッセージを受け取り、リフレクションによって Facepunch.Steamworks の C# API を呼び出します。

```
JavaScript (ES6 Proxy)
  └─ Host.Steam.ClassName.MethodName(args)
        ↓ JSON { method: "Steam.ClassName.Method", params: { args: [...] } }
GenericDllPlugin (C#)
  └─ Steamworks.ClassName.MethodName(args) ← Facepunch.Steamworks
        ↓ 戻り値
JavaScript (Promise)
```

### 特徴
- **API の追加が不要**: Facepunch.Steamworks が提供するすべてのクラス・メソッド・プロパティに、JavaScript から直接アクセスできます。
- **標準的な呼び出し**: 公式の C# ドキュメントと同じ名前でメソッドを呼び出せます。
- **イベント受信**: Steam 側で発生したコールバックイベント（実績解除の通知など）を JavaScript で受信できます。

---

## JavaScript からの呼び出し方

**Facepunch.Steamworks の公式 C# ドキュメントのクラス名・メソッド名をそのまま指定するだけです。**

```js
// 1. 初期化
await Host.Steam.SteamClient.Init(480, true);

// 2. 実績の解除
await Host.Steam.SteamUserStats.SetAchievement('ACH_WIN_ONE_GAME');
await Host.Steam.SteamUserStats.StoreStats();

// 3. 統計（Stats）の取得
const wins = await Host.Steam.SteamUserStats.GetStatInt('NumWins');

// 4. Steam オーバーレイを開く
await Host.Steam.SteamFriends.OpenOverlay('achievements');

// 5. スクリーンショットの保存
// WebView2 の画面をキャプチャしてファイル保存し、それを Steam に登録します
const preview = await Host.Internal.WebView.CapturePreview("screenshot.png");
await Host.Steam.SteamScreenshots.AddScreenshot(
    preview.path, "", preview.width, preview.height
);

// 6. 非同期メソッドとインスタンスの利用
const board = await Host.Steam.SteamUserStats.FindOrCreateLeaderboardAsync('Feet Traveled', 2, 1);
await board.SubmitScoreAsync(100);
```

参照先: [Facepunch.Steamworks ドキュメント](https://wiki.facepunch.com/steamworks/)

---

## Steam コールバックイベント

Steam 側で発生したイベントは `Host.on()` で受信できます。

```js
// 実績の進捗通知
Host.on('OnAchievementProgress', ({ achievementName, currentProgress, maxProgress }) => {
    console.log(`${achievementName}: ${currentProgress}/${maxProgress}`);
});

// Steam オーバーレイの開閉
Host.on('OnGameOverlayActivated', ({ active }) => {
    if (active) pauseGame();
    else resumeGame();
});
```

---

## 制約

- **巨大なデータの禁止**: ホストと JavaScript 間の通信制限により、スクリーンショットなどの巨大な画像データを数値配列として直接やり取りすることはできません。
- **オーバーレイ**: WebView2 の画面上に Steam オーバーレイは重なりません（OS の通知等のみ表示されます）。
- **プラットフォーム**: Steam Deck / SteamOS は非対応です。
