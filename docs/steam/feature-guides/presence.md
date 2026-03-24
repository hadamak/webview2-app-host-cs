# Rich Presence

Rich Presence は、Steam のフレンド一覧に「いま何をしているか」を表示する機能です。

向いている用途:

- タイトル画面
- ステージ選択中
- 協力プレイのロビー待機中
- ボス戦挑戦中

## 例

```js
Steam.setRichPresence('status', 'Stage 3');
```

## 消去

```js
Steam.clearRichPresence();
```

## 補足

これはゲーム内 UI ではなく、Steam 側の表示用情報です。
