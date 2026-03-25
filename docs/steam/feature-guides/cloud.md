# Steam Cloud

Steam Cloud は、ファイルを Steam 経由で同期する仕組みです。

## Steamworks 側の事前設定

> **コードを書く前に必要な設定です。**
>
> 1. Steamworks Partner サイト → App Admin → **Steam Cloud**
> 2. 「Enable Cloud Support for \<App Name\>」を有効にする
> 3. クォータ（バイト数・ファイル数）を設定する
> 4. **Publish** を実行する
>
> 上記なしにこのページの API を呼んでも、`writeCloudFileText` 等は常に失敗します。  
> 参照: [Steam Cloud — Steamworks ドキュメント](https://partner.steamgames.com/doc/features/cloud)

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
