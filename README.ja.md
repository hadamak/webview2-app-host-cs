# WebView2AppHost

[![Build](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml/badge.svg)](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/hadamak/webview2-app-host-cs)](https://github.com/hadamak/webview2-app-host-cs/releases/latest)
[![License: MIT](https://img.shields.io/github/license/hadamak/webview2-app-host-cs)](LICENSE)

WebView2AppHost は、HTML / CSS / JavaScript で作った Web アプリを、WebView2 ベースの小さな Windows デスクトップアプリとして配布するためのホストです。

このプロジェクトには 2 つの見方があります。

- 導入者としては、ホスト側の専門知識なしに Web コンテンツをそのまま配布に使える
- メンテナとしては、コネクタ、AI 連携、低レベルなブラウザ制御まで拡張できる

この README はまず導入者向けです。

> English README: [README.md](README.md)

## なぜ使うのか

- Chromium を丸ごと同梱しない
  - 重いブラウザランタイムを抱えずに済む
- 基本用途ではバックエンド不要
  - 静的な Web コンテンツを配るだけなら C#、Node.js、Python の準備は不要
- ホストを再ビルドせずにローカル機能を足せる
  - DLL や外部実行ファイルを横に置き、設定だけで有効化できる
- コンテンツ差し替えだけならホストの再ビルドも不要
  - 既存の EXE に `www\`、ZIP、DLL、外部実行ファイルを添える形で使える
- 標準 Web API がそのまま使いやすい
  - `window.close()`、fullscreen、リンク、ダイアログ、クリップボード、メディア再生など
- 必要になった時だけ拡張できる
  - DLL、Sidecar、Pipe、MCP などは任意機能

## 最短の使い方

Web アプリをデスクトップ EXE として配布したいなら、まずは次で十分です。

1. ビルド済みの `WebView2AppHost.exe` を用意する
2. EXE の隣に `www\` フォルダを置く
3. `www\app.conf.json` を置く
4. EXE を起動する

この使い方なら、ホスト本体をビルドする必要はありません。さらに、アプリがローカル機能を必要とする場合でも、DLL や外部実行ファイルを横に置いて設定するだけで対応できるケースが多く、ホスト自体の再ビルドは不要です。

## 基本レイアウト

```text
WebView2AppHost.exe
WebView2AppHost.exe.config
Microsoft.Web.WebView2.Core.dll
Microsoft.Web.WebView2.WinForms.dll
WebView2Loader.dll
www/
  index.html
  app.conf.json
  assets/
  scripts/
```

最小の `app.conf.json`:

```json
{
  "title": "My App",
  "window": { "width": 1280, "height": 720, "frame": true },
  "url": "https://app.local/index.html"
}
```

## 再ビルド不要の拡張モデル

このホストの特徴は、静的コンテンツだけに限らないことです。ビルド済みのホストに対して、設定と同梱ファイルだけでローカル機能を足せます。

- HTML / CSS / JavaScript だけを配る
- .NET DLL を EXE の横に置いて JavaScript から呼ぶ
- Node.js、Python、PowerShell、独自実行ファイルを横に置いてサイドカーとして使う
- 必要なコネクタ機能がホストに入っていれば、ホスト自体は再ビルドしない

典型例:

```text
WebView2AppHost.exe
www/
  index.html
  app.conf.json
plugins/
  SystemMonitor.dll
tools/
  agent.js
```

設定例:

```json
{
  "title": "My App",
  "window": { "width": 1280, "height": 720, "frame": true },
  "url": "https://app.local/index.html",
  "connectors": [
    { "type": "dll", "path": "plugins/SystemMonitor.dll" },
    { "type": "sidecar", "runtime": "node", "script": "tools/agent.js" }
  ]
}
```

要点は、ホストを差し替えずに、同梱したファイルと設定だけでローカルコード能力を増やせる点です。

## コンテンツ配置の選択肢

導入者として主に意識すれば良いのは次の配置です。

- EXE 隣接の `www\`
  - 開発中の差し替えや、そのままの配布に最も向く
- コマンドライン引数で渡す ZIP
  - EXE を共通シェルとして使いたい時に向く
- EXE と同名の ZIP
  - コンテンツだけ差し替えたい時に向く
- EXE 末尾に連結した ZIP
  - 単一ファイルで配布したい時に向く
- 埋め込み `app.zip`
  - 自前でホストをビルドする場合の既定コンテンツに向く

優先順位は次のとおりです。

1. `www\`
2. コマンドライン ZIP
3. 同名 ZIP
4. 連結 ZIP
5. 埋め込み `app.zip`

詳細:

- [docs/guides/content-packaging.md](docs/guides/content-packaging.md)

## ビルドと配布

Web コンテンツだけを更新する場合、ホストの再ビルドは不要なことが多いです。

ホストをビルドする場合:

```powershell
msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64
```

ビルド構成:

- `Debug`
  - ローカル開発向け。`test-www\` を出力先 `www\` にコピー
- `Release`
  - 通常の配布向け
- `SecureRelease`
  - MCP、Sidecar、Pipe、CDP を除外した制限付きビルド

詳細:

- [docs/guides/build-and-release.md](docs/guides/build-and-release.md)

## リリース ZIP に含まれるもの

`tools/package-release.ps1` が作る ZIP には次が含まれます。

- ホスト EXE と config
- WebView2 関連 DLL
- 既定の `www\`
- `README.md`、`README.ja.md`
- `LICENSE`、`THIRD_PARTY_NOTICES.md`
- `docs\` の一般公開向け部分
- `samples\`

含まれないもの:

- Node.js ランタイム
- Python ランタイム
- Steam などの外部バイナリ

必要な場合だけ、アプリ側で追加同梱します。

## 必要になった時の拡張

基本用途では不要ですが、必要になった時は、ホストを再ビルドせずに次の拡張を有効化できることがあります。

- `DllConnector`
  - .NET DLL を横に置いて JavaScript から呼ぶ
- `SidecarConnector`
  - Node.js、Python、PowerShell などの外部実行ファイルやスクリプトと通信する
- Pipe connectors
  - 他の Windows プロセスと連携する
- MCP
  - ローカルツールやコンテンツを AI クライアントへ公開する

ガイド:

- [docs/guides/generic-dll-plugin.md](docs/guides/generic-dll-plugin.md)
- [docs/guides/generic-sidecar-plugin.md](docs/guides/generic-sidecar-plugin.md)
- [docs/guides/steam-integration.md](docs/guides/steam-integration.md)

## 設定

`app.conf.json` は構造化形式のみを受け付けます。

```json
{
  "title": "My App",
  "window": { "width": 1280, "height": 720, "frame": true },
  "url": "https://app.local/index.html",
  "proxy_origins": ["https://api.github.com"],
  "steam": { "app_id": "480", "dev_mode": true },
  "navigation_policy": {
    "external_navigation_mode": "rules",
    "open_in_host": ["*.github.com"],
    "block_request_patterns": ["*ads*"]
  }
}
```

仕様:

- [docs/api/app-conf-json.md](docs/api/app-conf-json.md)

## サンプル

- `samples/sidecar-node`
- `samples/sidecar-python`
- `samples/sidecar-powershell`
- `samples/steam-complete`

## メンテナ向け

内部設計、互換性、将来の改修メモは導入者向け説明から分離しています。

入口:

- [docs/maintainer/README.md](docs/maintainer/README.md)

## ライセンス

- [LICENSE](LICENSE)
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
