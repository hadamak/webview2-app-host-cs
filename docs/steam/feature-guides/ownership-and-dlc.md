# 所有権確認 / DLC

この機能は、ユーザーが本編や DLC を持っているか、導入済みかを確認するためのものです。

向いている用途:

- DLC ステージの開放判定
- サウンドトラックや追加キャラクターの有効化
- Family Sharing 判定の補助

## 所有権確認

```js
const info = await Steam.getAppOwnershipInfo();
console.log(info.isSubscribed);
console.log(info.isSubscribedFromFamilySharing);
```

## 特定 AppID の確認

```js
const result = await Steam.isSubscribedApp(123456);
console.log(result.isSubscribed);
```

## DLC 一覧

```js
const result = await Steam.getDlcList();
console.log(result.dlc);
```

## DLC の導入状態

```js
const result = await Steam.checkDlcInstalled([1000, 1001]);
console.log(result.results);
```
