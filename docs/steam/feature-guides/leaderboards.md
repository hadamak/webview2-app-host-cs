# Leaderboards

Leaderboards は Steam のランキング機能です。

向いている用途:

- ハイスコア
- タイムアタック
- ステージ別ランキング

## Steamworks 側の事前設定

> **コードを書く前に必要な設定です。**
>
> 1. Steamworks Partner サイト → App Admin → **Leaderboards**
> 2. ボードを作成する（名前・ソート順・表示形式）
> 3. **Publish** を実行する
>
> `findOrCreateLeaderboard` は名前が一致するボードが存在しない場合に作成を試みますが、  
> 本番環境では管理画面で事前定義したボードのみが有効です。  
> 未定義のままスコアを送信しても Steamworks 側には反映されません。

## 基本の流れ

1. leaderboard を見つける、または作る
2. handle を受け取る
3. スコアを送信する
4. エントリ一覧を取得する

## 例

```js
const board = await Steam.findOrCreateLeaderboard(
    'Feet Traveled',
    'descending',
    'numeric'
);

await Steam.uploadLeaderboardScore(board.leaderboardHandle, 5000);

const scores = await Steam.downloadLeaderboardEntries(
    board.leaderboardHandle,
    'global',
    0,
    9
);
```

## よくある失敗

- Steamworks 側でボードが未定義、または Publish されていない
- leaderboard 名が Steam 側定義と一致していない
- handle を取得する前に upload / download している

## 補足

ランキング名は Steamworks 側に合わせて管理してください。
