# 実績

実績は、プレイヤーが特定条件を満たしたときに Steam 上で解除される項目です。

向いている用途:

- ボス撃破
- 特定エンディング到達
- やり込み条件の達成

## 最小例

```js
const steam = await Steam.init();
if (!steam.isAvailable) return;

await Steam.unlockAchievement('ACH_WIN_ONE_GAME');
```

## 取得

```js
const result = await Steam.getAchievementState('ACH_WIN_ONE_GAME');
console.log(result.isUnlocked);
```

## よくある失敗

- 実績 API 名が Steamworks 側の登録名と一致していない
- `Steam.init()` 前に呼んでいる

## 補足

実績そのものの設定は Steamworks 側で行います。  
この API は、登録済みの実績を解除・確認するためのものです。
