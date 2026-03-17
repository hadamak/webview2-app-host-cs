# 改定案（ドキュメント・コードレビュー結果）

本ドキュメントは、現行実装（`src/`）と README 記述の整合を確認したうえで、改定候補を優先度順にまとめたものです。

## 1. 反映済みの修正

### Program.cs — 起動失敗メッセージの説明不足を解消
`app.zip` 前提になっていたエラーメッセージを、実際の探索順（個別配置 / 引数 ZIP / 隣接 ZIP / EXE 結合 / 埋め込み）に合わせて更新。

### Program.cs — 設定ファイル名を `app.conf.json` に一本化
`app.config.json` へのフォールバックを削除。

### App.cs — `#if DEBUG` による DevTools 自動制御
Debug ビルドで `AreDevToolsEnabled` / `IsStatusBarEnabled` を自動的に有効化。Release ビルドでは無効のまま。

### App.cs — `NativeMethods.DestroyIcon` を削除
どこからも呼ばれていない P/Invoke 宣言を除去。favicon 管理は `Icon.Dispose()` で完結している。

### App.cs / web-content — `GameBridge` → `AppBridge` に改名
ゲーム専用を連想させる命名を汎用的な `AppBridge` へ変更。`main.js`・`index.html`・`README.md` も合わせて更新。後方互換エイリアスなし。

### AppConfig.cs — バリデーション強化
- `Title`: null・空白・制御文字（`\p{C}`）を除去してトリム。空になればデフォルト値に戻す。
- `Width` / `Height`: `160〜7680` / `160〜4320` にクランプ。

### App.cs — Range Request に 416 応答を追加
`ParseRange()` を `(long, long)?` 戻り値に変更。逆転レンジ・`bytes=-0` で `null` を返し、呼び出し側で `416 Range Not Satisfiable` + `Content-Range: bytes */<total>` を返す。`end > total` のクランプは動画シーク互換のため維持。

### App.cs — フルスクリーン API の整理（標準 HTML5 API への完全移行）
- `ContainsFullScreenElementChanged` イベントによるウィンドウ状態の自動同期を導入。
- `AppBridge.requestFullscreen` / `exitFullscreen` ラッパーを廃止。JS 側から標準の `requestFullscreen()` を呼び出すだけで、ホスト側のウィンドウ状態（ボーダレス・最大化）が自動的に連動するように変更。
- 手動の `fullscreenChange` イベント通知および `document.fullscreenElement` の上書き処理を削除し、ブラウザ標準の動作へ一本化。
- C# 側の `ProcessCmdKey`（F11/ESC フック）を削除。F11 による切り替えは JS 側で `requestFullscreen()` を呼び出すことで、他のフルスクリーン要求と同じパスで一貫して処理される。

### App.cs — アプリ終了処理の標準化 (`window.close()` 対応)
- `WindowCloseRequested` イベントをハンドルするように変更し、JS 側の `window.close()` 呼び出しでアプリを終了できるように対応。
- `AppBridge.exitApp()` を廃止。
- JS->C# のブリッジ通信（`WebMessageReceived`）が不要になったため、`SetupHostObjectBridge`, `OnWebMessageReceived` および `WebMessage` クラスを完全に削除。
- `System.Runtime.Serialization` への依存を排除（`AppConfig` は独立して維持）。

### App.cs — 外部リンクのブラウザ転送 (`NewWindowRequested`)
- `https://app.local/` 以外のドメインへの遷移・新規ウィンドウ要求を検知し、自動的に OS 既定のブラウザで開くように実装。
- `target="_blank"` や `window.open()` が特別な API なしに期待通り動作する。

### App.cs — `beforeunload` への標準対応
- ウィンドウの「×」ボタン押下時に直接終了せず、一旦 `window.close()` を実行するように `OnFormClosing` を修正。
- これにより JS 側の `window.onbeforeunload` ハンドラが透過的に機能し、標準の確認ダイアログが表示される。
- ユーザーが終了を承諾した場合のみ `WindowCloseRequested` を経由して実際にフォームが閉じる。

### ZipContentProvider.cs — STORED パスとリフレクションを削除
ZIP エントリへの Range Request 対応（STORED 判定・`_offsetOfLocalHeader` リフレクション・`SubStream` 返し）を削除。Range Request が必要なファイルは `www/` 配置で対応する方針に統一。`System.Reflection`（`Assembly` 除く）・`System.Linq` の using も除去。

### pack_zip.py — make_zip.py に置き換え
STORED/DEFLATED 混在ロジックが不要になったため削除。全件 DEFLATED で ZIP 化する `make_zip.py` に置き換え。`csproj` の `PackAppZip` ターゲットおよび `release.yml` も合わせて更新。

### .github/workflows — `setup-dotnet` を除去・リリースパッケージ構成を刷新
NuGet パッケージ取得を `msbuild /t:Restore` に統合。リリースパッケージは `bin/` サブフォルダ廃止・DLL をルートに展開・デモコンテンツを `www/` として同梱・`scripts/bundle.py` のみ同梱。GitHub Release アセットは `.zip` と `.sha256` のみ。

---

## 2. 対応しない項目

### .NET 8 への移行
エンドユーザーが別途ランタイムをインストールする必要が生じるため、`net472`（Windows 標準搭載）を維持する。

### ZIP エントリへの Range Request 対応
Range Request が必要なファイル（動画・音声・wasm）は `www/` フォルダへの個別配置で対応する運用に統一。
