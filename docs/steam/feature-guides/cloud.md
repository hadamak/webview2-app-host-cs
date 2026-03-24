# Steam Cloud

Steam Cloud は、ファイルを Steam 経由で同期する仕組みです。

向いている用途:

- セーブデータ
- 設定ファイル
- JSON の進行状況

## テキスト保存の例

```js
await Steam.writeCloudFileText(
    'save.json',
    JSON.stringify({ level: 3, hp: 42 })
);
```

## 読み出しの例

```js
const result = await Steam.readCloudFileText('save.json');
console.log(result.text);
```

## 一覧取得

```js
const result = await Steam.listCloudFiles();
console.log(result.files);
```

## 事前に確認したいこと

- Steam Cloud が有効か
- どのファイルを同期対象にするか
- JSON で十分か、独自バイナリにするか

## 補足

文字列以外のデータを扱いたい場合は `writeCloudFile()` / `readCloudFile()` を使います。
