# GenericSidecarPlugin — 外部プロセスをサイドカーとして実行・通信する

## 概要

`WebView2AppHost.GenericSidecarPlugin.dll` を追加することで、
Node.js や Python、自作の実行ファイルなど、**任意の外部プロセスを「サイドカー」として起動し、標準入出力を介して JS と通信** できるようになる。

これにより、WebView2（ブラウザ環境）からは直接実行できない重い処理や、OS 固有の機能、サーバーサイド言語の資産を安全に利用できる。

---

## 配置構成

```
MyApp.exe
WebView2AppHost.GenericSidecarPlugin.dll   ← 新規追加
node-runtime/
  node.exe                                 ← 実行ファイル
  server.js                                ← スクリプト
app.conf.json
www/
  index.html
```

---

## app.conf.json の設定

```json
{
  "plugins": ["GenericSidecarPlugin"],

  "sidecars": [
    {
      "alias": "NodeBackend",
      "executable": "node-runtime/node.exe",
      "workingDirectory": "node-runtime",
      "args": ["server.js"],
      "waitForReady": true
    }
  ]
}
```

### sidecars 設定項目

| キー | 型 | 説明 |
|------|----|------|
| `alias` | string | JS からメッセージを送る際の宛先名。一意である必要がある。 |
| `executable` | string | 実行ファイルのパス。app.conf.json からの相対パスまたは絶対パス。 |
| `workingDirectory`| string | プロセスの作業ディレクトリ。省略時は EXE の場所。 |
| `args` | string[] | 起動引数の配列。 |
| `waitForReady` | bool | `true` の場合、サイドカーから最初のメッセージ（Ready通知）が来るまで、JS からの送信をキューイングする。 |

---

## 通信プロトコル

JS とサイドカーの間で、**改行区切りの JSON (NDJSON)** を使って双方向にメッセージをやり取りする。

### 1. JS → サイドカー

`window.chrome.webview.postMessage` を使用する。
`source` フィールドに、`app.conf.json` で指定した `alias` を設定する。

```js
// JS 側
window.chrome.webview.postMessage({
  source: "NodeBackend",
  method: "calculate",
  params: { a: 1, b: 2 }
});
```

C# ホスト（プラグイン）はこのメッセージを受け取ると、該当するサイドカープロセスの **標準入力 (stdin)** へそのまま JSON を書き込む。

### 2. サイドカー → JS

サイドカープロセスが **標準出力 (stdout)** に JSON を書き込むと、C# ホストがそれを受け取り、そのまま WebView2 の `window.chrome.webview.addEventListener('message', ...)` へ転送する。

```js
// サイドカー（Node.js）側
process.stdout.write(JSON.stringify({
  source: "NodeBackend",
  result: 3
}) + "\n");
```

```js
// JS 側（受信）
window.chrome.webview.addEventListener('message', (e) => {
  if (e.data.source === "NodeBackend") {
    console.log("Result from Node:", e.data.result);
  }
});
```

---

## サイドカーの実装サンプル

サイドカー側では、標準入力から NDJSON を読み込み、標準出力へ結果を書き出すループを実装します。

### Node.js (server.js)
`node-runtime/server.js` に完全なサンプルがあります。

```js
const fs = require('fs').promises;

// ハンドラの登録
const handlers = {
  async listFiles(dir) {
    return await fs.readdir(dir);
  }
};

// メインループ
process.stdin.on('data', (data) => {
  const msg = JSON.parse(data);
  const result = handlers[msg.method.split('.')[2]](...msg.params);
  process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id: msg.id, result }) + '\n');
});

// 起動完了通知
process.stdout.write(JSON.stringify({ ready: true }) + '\n');
```

### Python (server.py)
`python-runtime/server.py` に完全なサンプルがあります。

```python
import sys, json

def dispatch(msg):
    # ロジックを実行し結果を stdout へ
    result = {"jsonrpc": "2.0", "id": msg['id'], "result": "hello from python"}
    print(json.dumps(result))

# 起動完了通知
print(json.dumps({"ready": True}))

for line in sys.stdin:
    dispatch(json.loads(line))
```

---

## 特徴とメリット

- **言語を問わない**: 標準入出力が扱えるなら、Node.js, Python, Go, Rust, C++ など何でもサイドカーにできる。
- **セキュリティ**: サイドカーは別プロセスとして動くため、ブラウザ側（WebView2）の脆弱性がシステム全体に波及するリスクを抑えられる。
- **疎結合**: プロトコルが JSON であるため、ホスト側のコードを変更せずにサイドカー側の実装を差し替えられる。

---

## 注意事項

- **ライフサイクル**: サイドカープロセスは、メインのホストアプリ（EXE）が終了する際に自動的に終了（Dispose）される。
- **バッファリング**: 標準出力は改行 (`\n`) を受信するまでホスト側でバッファリングされる。必ずメッセージごとに改行を出力すること。
- **ログ**: サイドカーの標準エラー出力 (stderr) は、ホストのログ（`AppLog`）に記録される。

---

## ビルド方法

```powershell
# プラグイン DLL 単体ビルド
dotnet build src-generic/WebView2AppHost.GenericSidecarPlugin.csproj -c Release
```
