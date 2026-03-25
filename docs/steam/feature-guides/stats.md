# User Stats

User Stats は、Steam に保存される数値データです。  
実績より柔軟で、累計値や内部記録の保存に向いています。

向いている用途:

- 総プレイ回数
- 総撃破数
- 累計距離
- ベストタイム

## Steamworks 側の事前設定

> **コードを書く前に必要な設定です。**
>
> 1. Steamworks Partner サイト → App Admin → **Stats**
> 2. stat を定義する（API 名・型 `INT` or `FLOAT`・デフォルト値）
> 3. **Publish** を実行する
>
> 未定義・未 Publish の stat 名を渡した場合、`setStat*` / `getStat*` は `stat-not-found-or-type-mismatch` を返します。  
> int / float の型指定が Steamworks 側定義と一致しない場合も同様です。

## Int stat の例

```js
await Steam.setStatInt('NumGames', 10);
await Steam.storeStats();
```

```js
const result = await Steam.getStatInt('NumGames');
console.log(result.value);
```

## Float stat の例

```js
await Steam.setStatFloat('FeetTraveled', 1234.5);
await Steam.storeStats();
```

## よくある失敗

- Steamworks 側で stat が未定義、または Publish されていない
- stat 名が未定義
- int / float の型が一致していない

失敗時は `reason: "stat-not-found-or-type-mismatch"` が返ることがあります。

## 補足

`setStat*()` のあと、永続保存したい場合は `storeStats()` も呼んでください。
