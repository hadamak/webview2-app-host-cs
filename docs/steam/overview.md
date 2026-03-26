# Steam 対応の概要

このドキュメントは、WebView2AppHost の新しい Steam ブリッジアーキテクチャの概要です。

---

## 新しいパラダイム：完全パススルー型ブリッジ

従来の `steam_bridge.dll`（C++ 実装）は廃止されました。  
バックエンドに **[Facepunch.Steamworks](https://wiki.facepunch.com/steamworks/)** を採用し、
JavaScript から直接 Facepunch.Steamworks の C# API を呼び出せる**汎用パススルー型ブリッジ**に刷新されています。

```
JavaScript (ES6 Proxy)
  └─ Steam.ClassName.MethodName(args)
        ↓ JSON { className, methodName, args }
C# ディスパッチャ (リフレクション)
  └─ Steamworks.ClassName.MethodName(args) ← Facepunch.Steamworks
        ↓ 戻り値
JavaScript (Promise)
```

### 何が変わったか

| 項目 | 旧（C++ ブリッジ） | 新（Facepunch.Steamworks） |
|------|-------------------|---------------------------|
| バックエンド | カスタム C++ DLL | Facepunch.Steamworks (NuGet) |
| JS 側 | 手書き API ラッパー群 | ES6 Proxy による自動パススルー |
| API 追加コスト | JS + C# + C++ の3箇所を変更 | **0行**（追加不要） |
| スクリーンショット | Base64 変換あり | RGB バイト配列を直接渡す |
| Steam なし環境 | DLL ロードを遅延 | DLL 不在でもクラッシュしない |

---

## JavaScript からの呼び出し方

**Facepunch.Steamworks の公式 C# ドキュメントのクラス名・メソッド名をそのまま指定するだけです。**

```js
// 実績の解除
await Steam.SteamUserStats.SetAchievement('ACH_WIN_ONE_GAME');
await Steam.SteamUserStats.StoreStats();

// 整数 Stat の取得
const wins = await Steam.SteamUserStats.GetStatInt('NumWins');

// フロート Stat の設定
await Steam.SteamUserStats.SetStat('FeetTraveled', 123.4);

// Rich Presence の設定
await Steam.SteamFriends.SetRichPresence('status', 'Stage 3');

// Steam オーバーレイを開く (OpenOverlay)
await Steam.SteamFriends.OpenOverlay('achievements');

// スクリーンショットのトリガー（WebView2 をキャプチャして Steam へ登録）
await Steam.SteamScreenshots.TriggerScreenshot();

// オブジェクト/インスタンスの利用（汎用インスタンス・ディスパッチャ）
const board = await Steam.SteamUserStats.FindOrCreateLeaderboardAsync('Feet Traveled', 2, 1);
await board.SubmitScoreAsync(100);
```

参照先: [Facepunch.Steamworks ドキュメント](https://wiki.facepunch.com/steamworks/)

---

## Steam コールバックイベント

C# 側で登録された Facepunch.Steamworks のコールバックは `Steam.on()` で受信できます。

```js
// 実績の進捗通知
Steam.on('OnAchievementProgress', ({ achievementName, currentProgress, maxProgress }) => {
    console.log(`${achievementName}: ${currentProgress}/${maxProgress}`);
});

// Steam オーバーレイの開閉
Steam.on('OnGameOverlayActivated', ({ active }) => {
    if (active) pauseGame();
    else resumeGame();
});

// マイクロトランザクション認証
Steam.on('OnMicroTxnAuthorizationResponse', ({ orderId, authorized }) => {
    if (authorized) fulfillOrder(orderId);
});
```

---

## 何ができるか

Facepunch.Steamworks が対応しているすべての Steam API を呼び出せます。

- **実績 (SteamUserStats)** — SetAchievement, ClearAchievement, StoreStats
- **User Stats (SteamUserStats)** — GetStatInt, GetStatFloat, SetStat
- **Steam Cloud (SteamRemoteStorage)** — FileWrite, FileRead, FileDelete
- **Leaderboards (SteamUserStats)** — FindOrCreateLeaderboardAsync, SubmitScoreAsync (インスタンスメソッド)
- **Rich Presence (SteamFriends)** — SetRichPresence, ClearRichPresence
- **オーバーレイ (SteamFriends)** — OpenOverlay, OpenWebOverlay
- **所有権・DLC (SteamApps)** — IsSubscribedApp, IsDlcInstalled
- **スクリーンショット (SteamScreenshots)** — TriggerScreenshot (C# 側でキャプチャ・登録)
- その他 Facepunch.Steamworks が公開する全クラス

---

## ⚠️ コードを書く前に必要な Steamworks 管理画面の設定

各機能は Steamworks Partner サイト（App Admin）での事前設定が必要です。

| 機能 | 必要な事前設定 |
|------|---------------|
| 実績 | App Admin → Achievements → 定義 → **Publish** |
| User Stats | App Admin → Stats → 定義 → **Publish** |
| Leaderboards | App Admin → Leaderboards → 作成 → **Publish** |
| Steam Cloud | App Admin → Steam Cloud → 有効化 → **Publish** |

---

## 制約

- WebView2 の画面上に Steam オーバーレイは重なりません
- Steam Deck / SteamOS は非対応（Windows 版 WebView2 専用）
- Facepunch.Steamworks が未対応の低水準 API は呼び出せません

---

## 次に読む文書

- 最短導入手順: `docs/steam/getting-started.md`
