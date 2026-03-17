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

### App.cs — WebMessage の JSON パースを DTO 化
`OnWebMessageReceived()` の正規表現抽出を `DataContractJsonSerializer`（`AppConfig` と同じシリアライザ）による DTO デシリアライズに置き換え。未知コマンドを `Debug.WriteLine` でログ出力。

### App.cs — `appClosing` 廃止
ウィンドウクローズへのホスト側介入を廃止。カスタム API の保守コスト・`window.addEventListener` 上書きによるサードパーティ干渉リスクを解消。クローズ前処理が必要なコンテンツは `AppBridge.exitApp()` 経由で自分のタイミングで閉じる運用に。`_closePending` / `_closeConfirmed` フラグ・`confirmClose` / `cancelClose` コマンド・JS 側の追跡ロジックをすべて削除。

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
