# GenericDllPlugin — 素の DLL を置くだけで JS から呼ぶ

## 概要

`WebView2AppHost.GenericDllPlugin.dll` を追加することで、
**任意の .NET DLL を app.conf.json に書くだけ** で JS から呼び出せるようになる。

SteamBridgeImpl が Steamworks.* に行っていたリフレクション・ディスパッチを
任意の DLL に対して汎用化したプラグイン。

---

## 配置構成

```
MyApp.exe
WebView2AppHost.GenericDllPlugin.dll   ← 新規追加
SQLite.dll                             ← JS から呼びたい任意の DLL
MyApp.BusinessLogic.dll               ← 同上
app.conf.json
www/
  index.html
```

---

## app.conf.json の設定

```json
{
  "plugins": ["GenericDllPlugin"],

  "loadDlls": [
    "SQLite.dll",
    { "alias": "MyLogic", "dll": "MyApp.BusinessLogic.dll" }
  ]
}
```

### loadDlls フォーマット

| 形式 | 記述例 | エイリアス |
|------|--------|-----------|
| 文字列（ファイル名のみ） | `"SQLite.dll"` | `SQLite`（拡張子除去） |
| オブジェクト（エイリアス明示） | `{"alias":"DB","dll":"SQLite.dll"}` | `DB` |

DLL のパスは **絶対パス** または **app.conf.json と同じディレクトリからの相対パス**。

---

## JS からの呼び出し方

すべてのメッセージは `source: "Host"` を使う。

### 静的メソッド

```js
// Host.SQLite.Database.QueryAll("SELECT * FROM items")
const rows = await Host.invoke({
  dllName: "SQLite",
  className: "Database",
  methodName: "QueryAll",
  args: ["SELECT * FROM items"]
});
```

### コンストラクタ（インスタンス生成）

```js
// new SqliteConnection("test.db")
const conn = await Host.invoke({
  dllName: "SQLite",
  className: "SqliteConnection",
  methodName: ".ctor",    // または "Create"
  args: ["test.db"]
});
// conn == { __isHandle: true, __handleId: 1, className: "SqliteConnection" }
```

### インスタンスメソッド（handleId を指定）

```js
await Host.invoke({ handleId: conn.__handleId, methodName: "Open" });
await Host.invoke({ handleId: conn.__handleId, methodName: "Execute",
                    args: ["INSERT INTO t VALUES (1)"] });
```

### ハンドル解放

```js
Host.release(conn);
// または直接:
// postMessage({ source:"Host", messageId:"release", params:{ handleId: conn.__handleId } })
```

### 静的プロパティ / フィールド取得

```js
// methodName にプロパティ名をそのまま渡す
const version = await Host.invoke({
  dllName: "MyLogic",
  className: "AppVersion",
  methodName: "Current"    // static string Current { get; }
});
```

---

## メッセージプロトコル詳細

### JS → C#（invoke）

```jsonc
{
  "source":    "Host",
  "messageId": "invoke",
  "asyncId":   42,
  "params": {
    // --- 静的呼び出し ---
    "dllName":    "SQLite",          // loadDlls で指定したエイリアス
    "className":  "Database",        // 型名（単純名 or 完全修飾名）
    "methodName": "QueryAll",        // メソッド/プロパティ/フィールド名
    "args":       ["SELECT 1"],      // 引数配列

    // --- インスタンス呼び出し（handleId を指定した場合 dllName/className 不要）---
    "handleId":   1,
    "methodName": "Execute",
    "args":       ["DROP TABLE t"]
  }
}
```

### C# → JS（invoke-result）

```jsonc
// 成功
{ "source": "Host", "messageId": "invoke-result", "asyncId": 42,
  "result": <値 or ハンドルオブジェクト> }

// エラー
{ "source": "Host", "messageId": "invoke-result", "asyncId": 42,
  "error": "エラーメッセージ" }
```

### ハンドルオブジェクト（C# 参照型を JS に返す場合）

```jsonc
{ "__isHandle": true, "__handleId": 1, "className": "SqliteConnection" }
```

使い終わったら必ず `release` メッセージを送り、C# 側のハンドルレジストリから解放する。

---

## ディスパッチ優先順位

1. `handleId` あり → **インスタンスメソッド / プロパティ / フィールド**
2. `methodName == ".ctor"` または `"Create"` → **コンストラクタ**
3. 静的メソッド（引数の数が一致するオーバーロードを優先）
4. 静的プロパティ getter（③で見つからない場合）
5. 静的フィールド（④で見つからない場合）

---

## 引数型変換（JS → C#）

| JS 型 | C# ターゲット型 | 変換方法 |
|-------|---------------|---------|
| `number` | `int`, `long`, `float`, `double`, `uint`, ... | `Convert.ChangeType` |
| `string` | `string` | そのまま |
| `string` | `enum` | `Enum.Parse`（大文字小文字不問） |
| `string` | byte[] | Base64 デコード |
| `boolean` | `bool` | そのまま |
| `Array<number>` | `byte[]` | 要素ごとに `Convert.ToByte` |
| `null` / `undefined` | 値型 | `default(T)` |

---

## 戻り値のラッピング（C# → JS）

| C# 型 | JS 表現 |
|-------|---------|
| `null` | `null` |
| primitive / `string` / `enum` | そのまま |
| `byte[]` | `number[]`（整数配列） |
| `IEnumerable` | `Array`（要素を再帰ラップ） |
| それ以外のクラス・構造体 | `{ __isHandle, __handleId, className }` |

---

## 注意事項

- ロードした DLL は `Assembly.LoadFrom` で読み込むため、DLL の依存アセンブリも
  EXE と同じディレクトリに配置するか、GAC に登録する必要がある。
- セキュリティ: loadDlls に指定できるのは EXE と同じプロセス内で動くコードのみ。
  信頼できない DLL を指定しないこと。
- `async` メソッドは `Task<T>` 戻り値として自動 await される。
- インスタンスが `IDisposable` を実装している場合、`Release` 時に `Dispose()` が呼ばれる。

---

## ビルド方法

```powershell
# プラグイン DLL 単体ビルド
dotnet build src-generic/WebView2AppHost.GenericDllPlugin.csproj -c Release

# ホスト本体のビルド（通常通り）
dotnet build src/WebView2AppHost.csproj -c Release
```

出力先の EXE フォルダに `WebView2AppHost.GenericDllPlugin.dll` と
呼び出したい DLL（例: `SQLite.dll`）を配置すれば動作する。
