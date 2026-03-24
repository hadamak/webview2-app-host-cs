# Leaderboards

Leaderboards は Steam のランキング機能です。

向いている用途:

- ハイスコア
- タイムアタック
- ステージ別ランキング

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

- leaderboard 名が Steam 側定義と一致していない
- handle を取得する前に upload / download している

## 補足

ランキング名は Steamworks 側に合わせて管理してください。
