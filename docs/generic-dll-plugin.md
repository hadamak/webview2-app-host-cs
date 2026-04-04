# GenericDllPlugin — 任意の .NET DLL を JS から呼び出す

## 概要

`WebView2AppHost.GenericDllPlugin.dll` を追加することで、
**任意の .NET DLL を app.conf.json に定義するだけ** で JavaScript から呼び出せるようになる。

内部ではリフレクションを使用しており、静的メソッド、コンストラクタ（インスタンス生成）、インスタンスメソッド、プロパティ、フィールド、さらには **.NET イベントの購読** まで幅広く対応している。

---

## 配置構成

```
MyApp.exe
WebView2AppHost.GenericDllPlugin.dll   ← プラグイン本体
SQLite.dll                             ← 呼び出したい DLL A
MyApp.BusinessLogic.dll               ← 呼び出したい DLL B
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
    {
      "alias": "MyLogic",
      "dll": "MyApp.BusinessLogic.dll",
      "exposeEvents": ["OnStateChanged", "OnDataReceived"]
    }
  ]
}
```

### loadDlls 設定項目

| キー | 型 | 説明 |
|------|----|------|
| (文字列) | string | DLL のファイル名。エイリアスは拡張子を除いた名前になる。 |
| `dll` | string | DLL のファイル名（相対パスまたは絶対パス）。 |
| `alias` | string | JS から呼び出す際の識別名（省略時は DLL 名）。 |
| `exposeEvents` | string[] | JS へ通知したい .NET イベント名のリスト。 |

---

## JS からの呼び出し方

標準の通信プロトコルとして **JSON-RPC 2.0** を使用する。

### 静的メソッドの呼び出し

`method` に `エイリアス.クラス名.メソッド名` を指定する。

```js
// Host.SQLite.Database.QueryAll("SELECT * FROM items")
window.chrome.webview.postMessage({
  jsonrpc: "2.0",
  id: 1,
  method: "SQLite.Database.QueryAll",
  params: { args: ["SELECT * FROM items"] }
});
```

### インスタンスの生成と利用

`methodName` に `.ctor` または `Create` を指定すると、インスタンスを生成し **ハンドルオブジェクト** を返す。

```js
// 1. インスタンス生成 (new SqliteConnection("test.db"))
const conn = await Host.invoke({
  dllName: "SQLite",
  className: "SqliteConnection",
  methodName: ".ctor",
  args: ["test.db"]
});
// 戻り値例: { __isHandle: true, __handleId: 1, className: "SqliteConnection" }

// 2. インスタンスメソッドの呼び出し (conn.Open())
// params に handleId を含めると、そのインスタンスに対して実行される。
window.chrome.webview.postMessage({
  jsonrpc: "2.0",
  id: 2,
  method: "SQLite.Open", // インスタンス呼び出し時はクラス名不要
  params: { handleId: conn.__handleId }
});
```

### ハンドルの解放

C# 側のメモリ（レジストリ）からオブジェクトを解放する。オブジェクトが `IDisposable` を実装している場合は `Dispose()` が呼ばれる。

```js
window.chrome.webview.postMessage({
  source: "Host",
  messageId: "release",
  params: { handleId: conn.__handleId }
});
```

---

## イベントの受信

`exposeEvents` で指定したイベントが .NET 側で発火すると、JS へメッセージが通知される。

```js
window.chrome.webview.addEventListener('message', (e) => {
  const msg = e.data;
  // { jsonrpc: "2.0", method: "MyLogic.OnStateChanged", params: { newState: "Active", ... } }
  if (msg.method === "MyLogic.OnStateChanged") {
    console.log("State changed to:", msg.params.newState);
  }
});
```

※ `EventHandler<T>` 等の標準的なイベントの場合、`T` のプロパティが `params` に展開される。

---

## 具体的な利用例: Steam 連携

`Facepunch.Steamworks` を使用して、JS から直接 Steamworks API を叩く例。

### 1. app.conf.json
```json
{
  "plugins": ["GenericDllPlugin"],
  "loadDlls": [
    {
      "alias": "Steam",
      "dll": "Facepunch.Steamworks.Win64.dll",
      "exposeEvents": ["OnAchievementProgress", "OnGameOverlayActivated"]
    }
  ],
  "steamAppId": "480"
}
```

### 2. JavaScript からの利用
```js
// 実績を解除する
await Steam.SteamUserStats.SetAchievement("ACH_WIN_ONE_GAME");
await Steam.SteamUserStats.StoreStats();

// イベントを受信する
window.chrome.webview.addEventListener('message', (e) => {
  if (e.data.method === "Steam.OnAchievementProgress") {
    const { achievementName, currentProgress, maxProgress } = e.data.params;
    console.log(`Achievement ${achievementName} progress: ${currentProgress}/${maxProgress}`);
  }
});
```

---

## 型変換の仕様

### 引数変換 (JS → C#)

| JS 型 | C# ターゲット型 | 変換・補足 |
|-------|---------------|-----------|
| `number` | `int`, `long`, `double`等 | `Convert.ChangeType` による自動変換。 |
| `string` | `string` | そのまま。 |
| `string` | `enum` | **文字列名**（大文字小文字不問）から変換可能。数値も可。 |
| `string` | `byte[]` | **Base64** 文字列としてデコード。 |
| `Array<number>` | `byte[]` | 数値配列として各要素を `byte` 変換。 |
| `boolean` | `bool` | そのまま。 |
| `string` | その他 | `T.Parse(string)` メソッド（Guid 等）があれば呼び出し。 |

### 戻り値変換 (C# → JS)

| C# 型 | JS 表現 | 変換・補足 |
|-------|---------|-----------|
| `null` | `null` | |
| primitive / `string` / `enum` | そのまま | |
| `byte[]` | `number[]` | JS で扱いやすいよう **整数配列** に変換される。 |
| `IEnumerable` | `Array` | 各要素を再帰的にラップして配列化。 |
| その他クラス / 構造体 | `Handle Object` | `{ __isHandle, __handleId, className }` に変換。 |

---

## 注意事項

- **セキュリティ**: `loadDlls` に指定できるのは、EXE と同じディレクトリ（またはサブディレクトリ）に存在する信頼できる DLL のみにすること。
- **依存関係**: ロードする DLL が他の DLL に依存している場合、それらも EXE と同じディレクトリに配置する必要がある。
- **非同期メソッド**: `Task` または `Task<T>` を返すメソッドは、ホスト側で自動的に `await` され、完了後の結果が JS に返される。
- **ライフサイクル**: アプリ終了時、すべてのハンドルオブジェクトに対して `Dispose()` が試みられる。
