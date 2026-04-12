# Connector Deep Dive: DLL & Sidecar

WebView2AppHost は、外部機能を統合するための 2 つの主要なコネクターを提供します。これらは `app.conf.json` の `connectors` 配列で定義され、JavaScript から共通の JSON-RPC 2.0 インターフェースで呼び出せます。

---

## 1. JavaScript 呼び出しインターフェース

### host.js の役割
`host.js` は、WebView2 の生のメッセージ通信（`postMessage`）を、直感的な JavaScript オブジェクト形式に抽象化するライブラリです。

1.  **ES6 Proxy による構文提供**: `Host.Alias.Class.Method()` というチェーン呼び出しを可能にします。
2.  **Promise の管理**: メッセージに一意の `id` を付与して送信し、ホストからの応答を待機して Promise を resolve/reject します。
3.  **イベント購読**: ホストからの通知を `Host.on('EventName', callback)` で受け取れるようにします。
4.  **ハンドルプロキシ化**: ホストから返されたインスタンスハンドル（`__isHandle: true` を含むオブジェクト）を検出し、自動的にメソッド呼び出しが可能なプロキシオブジェクトに変換します。
5.  **診断 API**: `Host.diagnostics.onMessage` により、通信されるすべての JSON データを監視できます。

### 呼び出し形式と解決ルール

#### A. 標準形式 (3階層)
```js
await Host.Steam.SteamUserStats.GetStatInt('NumWins');
```
- **JSON-RPC method**: `"Steam.SteamUserStats.GetStatInt"`
- **DLL**: `Steam` エイリアスの DLL 内の `SteamUserStats` クラスの静的メソッド `GetStatInt` を呼び出します。
- **Sidecar**: プロセスへそのまま文字列が送られ、サイドカー側のハンドラが解釈します。

#### B. クラス名の省略 (2階層)
```js
await Host.PythonRuntime.calculate(1, 2);
```
- **JSON-RPC method**: `"PythonRuntime.calculate"`
- **DLL**: クラス名が不明なため、解決に失敗します（DLL コネクターは通常クラス名を必要とします）。
- **Sidecar**: エイリアス直下の呼び出しとして扱われます。`server.py` などのサンプルでは、内部的にクラス名を持たないグローバルな関数として処理可能です。
- **インスタンス呼び出し**: ハンドルオブジェクト（プロキシ）に対する呼び出しは、内部的にこの 2 階層形式（`Alias.Method`）に `handleId` を添えて送信されます。

---

## 2. host.js を使用しない直接通信

`host.js` は必須ではありません。標準の WebView2 API を用いて自前で通信を実装することも可能です。

### 送信 (Request)
`window.chrome.webview.postMessage` を使用して JSON 文字列を送信します。

```js
// 自前での呼び出し例
window.chrome.webview.postMessage(JSON.stringify({
  jsonrpc: "2.0",
  id: 123,
  method: "Node.FileSystem.readFile",
  params: ["config.json"]
}));
```

### 受信 (Response / Notification)
`message` イベントを購読します。

```js
window.chrome.webview.addEventListener('message', event => {
  const msg = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
  
  if (msg.id === 123) {
    console.log("Result:", msg.result);
  } else if (!msg.id) {
    console.log("Notification:", msg.method, msg.params);
  }
});
```

---

## 3. DLL Connector

.NET アセンブリ（DLL）を同一プロセス内にロードし、リフレクションを用いて JavaScript から直接メソッドを呼び出します。

### 設定例
```json
{
  "type": "dll",
  "alias": "SystemTools",
  "path": "plugins/SystemHelper.dll",
  "expose_events": ["OnStatusChanged"]
}
```

### 特徴と仕様
- **呼び出し規約**: `Host.<Alias>.<Class>.<Method>(...args)`
- **静的 / インスタンス解決**:
    1. 指定されたクラスの静的メソッドを検索します。
    2. `methodName` が `.ctor` または `Create` の場合、コンストラクタを呼び出し、戻り値を「ハンドル」として管理します。
    3. ハンドルに対してメソッドを呼ぶと、そのインスタンスメンバーが実行されます。
- **型変換**: JavaScript の `number`, `string`, `boolean`, `array`, `object` を、C# の引数型（`int`, `double`, `string`, `byte[]`, `enum` 等）へ自動的にマッピングします。

---

## 4. Sidecar Connector

外部プロセスを起動し、標準入出力（stdio）を介して NDJSON (Newline Delimited JSON) で通信します。

### モードの比較: `streaming` vs `cli`

| 特徴 | `streaming` (既定) | `cli` |
| :--- | :--- | :--- |
| **プロセス寿命** | ホスト起動中に常駐。終了時は自動再起動。 | 呼び出しのたびに起動し、終了する。 |
| **通信方式** | 常に開いた `stdin`/`stdout` による通信。 | 引数によるコマンド実行 ＋ `stdout` の取得。 |
| **状態保持** | メモリ上の変数を保持可能。 | 状態を持てない（ステートレス）。 |

### 引数プレースホルダ (`cli` モード)

`cli` モードでは、JavaScript から渡された `params` をコマンドライン引数に埋め込めます。

- **`{key}`**: `params` オブジェクトの該当するキーの値で置換。
- **`{args}`**: 配列として渡された全引数をその位置に展開。
- **自動フラグ**: プレースホルダにないキーは自動的に `--key value` 形式で末尾に追加。

---

## 5. 共通仕様

### 文字エンコーディング
`SidecarConnector` は指定された `encoding` 名（`utf-8`, `shift-jis`等）を解決します。デフォルトは BOM なし UTF-8 です。

### プロセスのライフサイクル
- ホスト終了時、全サイドカープロセスには終了シグナルが送られ、3 秒後に強制終了されます。
- `streaming` モードでプロセスが予期せず終了した場合、最大 5 回まで自動再起動を試みます。
