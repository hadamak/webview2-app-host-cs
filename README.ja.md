# WebView2AppHost

[![Build](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml/badge.svg)](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/hadamak/webview2-app-host-cs)](https://github.com/hadamak/webview2-app-host-cs/releases/latest)
[![License: MIT](https://img.shields.io/github/license/hadamak/webview2-app-host-cs)](LICENSE)

WebView2AppHost は、HTML / CSS / JavaScript で構築された Web アプリを、WebView2 ベースの軽量な Windows デスクトップアプリとして配布するためのホストプログラムです。

**「透過的なホスト」** として設計されており、Web 標準の API がそのまま機能するため、開発者はデスクトップアプリ化のために独自のカスタム API を学習する必要はありません。

このプロジェクトには 2 つの側面があります：
- **導入者向け**: ホスト側のコードを一切書かずに、Web コンテンツをそのままデスクトップ EXE として配布できる。
- **拡張・メンテナ向け**: MessageBus を中心としたモダンな「コネクタアーキテクチャ」により、ネイティブ DLL、別言語のサイドカープロセス、さらには AI (MCP) 連携まで柔軟に拡張できる。

> English README: [README.md](README.md)

## 選ばれる理由

- **Web 標準 API への完全準拠**
  - `window.close()`、`requestFullscreen()`、`beforeunload` ダイアログ、`navigator.permissions`、クリップボード、メディア再生などが、通常のブラウザと全く同じように動作します。
- **再ビルド不要の拡張アーキテクチャ (Zero-Rebuild)**
  - ネイティブの機能が必要になっても、.NET DLL や Node.js / Python スクリプトを横に置き、`app.conf.json` に追記するだけで連携可能。ホスト EXE の再コンパイルは不要です。
- **AI 連携 (MCP) 標準対応**
  - Model Context Protocol (MCP) を第一級のコネクタとして内蔵。アプリの機能や UI を AI エージェントに簡単に公開できます（ヘッドレスモードやプロキシモードもサポート）。
- **透過的 CORS プロキシ**
  - CDP (Chrome DevTools Protocol) ベースのプロキシを内蔵し、ローカルオリジン (`https://app.local`) からでも外部 API へスムーズに `fetch()` できます。
- **超軽量・ポータブル**
  - Windows 標準搭載の .NET 4.8 ベース。巨大な Chromium バイナリを同梱する必要がありません。

## 最短の使い方

Web アプリをデスクトップ EXE として配布したいなら、以下の手順だけで完了します。

1. ビルド済みの `WebView2AppHost.exe` を用意する
2. EXE の隣に `www\` フォルダを作成し、Web コンテンツを置く
3. `www\app.conf.json` を配置する
4. EXE を起動する

この基本構成で、完全な Windows デスクトップアプリとして動作します。

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
  "url": "https://app.local/index.html",
  "connectors": [
    { "type": "browser" }
  ]
}
```

> **Tips:** ユーザーがウィンドウサイズやフルスクリーン設定だけを個別に変更したい場合、EXE の隣に `user.conf.json` を配置することで、開発者の `app.conf.json` を上書き（オーバーライド）できます。

## 再ビルド不要の拡張モデル

このホストの強力な点は、静的コンテンツの枠を超えた拡張性にあります。
アプリがシステム固有の能力を必要とした場合、コネクタ（`Connectors`）を追加するだけで機能を拡張できます。

- **`dll` コネクタ**: .NET DLL を EXE の横に置いて JavaScript から直接呼び出す（Steamworks 連携などに最適）。
- **`sidecar` コネクタ**: Node.js、Python、PowerShell、または独自実行ファイルを子プロセスとして起動し、JSON-RPC で通信する。
- **`pipe_server` / `mcp` コネクタ**: ローカルの自動化ツールや、AI エージェント（MCP クライアント）を接続する。

**設定例（拡張後）:**

```json
{
  "title": "My Extended App",
  "window": { "width": 1280, "height": 720 },
  "url": "https://app.local/index.html",
  "connectors": [
    { "type": "browser" },
    { "type": "dll", "alias": "Steam", "path": "Facepunch.Steamworks.Win64.dll" },
    { "type": "sidecar", "alias": "PythonRuntime", "runtime": "python", "script": "tools/agent.py", "wait_for_ready": true }
  ]
}
```

この変更においても、**ホスト本体の再ビルドは一切不要**です。

## コンテンツ配置の選択肢

コンテンツの配布方法は、要件に合わせて複数のスタイルから選べます（優先順位順）：

1. **EXE 末尾に連結した ZIP**: `copy /b EXE + ZIP`。単一ファイル配布に最適。検出時は外部の `app.conf.json` を無視する保護モードが働きます。
2. **コマンドライン ZIP**: `WebView2AppHost.exe patch.zip`。展開済みの環境を一時的に上書きする場合やシェルとしての利用に最適。
3. **EXE 隣接の `www\`**: 巨大なメディアファイル（動画・音声）の配置に最適。
4. **EXE と同名の ZIP**: `WebView2AppHost.zip`。コンテンツだけを後から更新したい場合に向く。
5. **埋め込み `app.zip`**: 内蔵の最終フォールバック。

詳細: [docs/guides/content-packaging.md](docs/guides/content-packaging.md)

## ビルドと配布

ホスト自体のビルドが必要な場合（独自アイコンの適用や埋め込み ZIP を作成する場合など）：

```powershell
msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64
```

ビルド構成:
- `Debug`: ローカル開発向け。`test-www\` を出力先 `www\` にコピーします。
- `Release`: 通常の配布向け。全機能が含まれます。
- `SecureRelease`: MCP、Sidecar、Pipe、CDP 等の拡張機能コードを完全に除外し、安全なオフライン専用に特化した制限付きビルドです。

配布用 ZIP の作成は付属のスクリプトで自動化できます:
```powershell
.\tools\package-release.ps1
```

## サンプル

同梱されている以下のサンプルですぐに仕組みを体験できます。

- `samples/sidecar-node` (Node.js との連携)
- `samples/sidecar-python` (Python との連携)
- `samples/sidecar-powershell` (PowerShell との連携)
- `samples/steam-complete` (Facepunch.Steamworks を用いたネイティブ DLL 連携)

## 開発・メンテナ向け

内部アーキテクチャ、API 対応状況、互換性についての詳細は、メンテナ向けのドキュメントに分離しています。
- [docs/maintainer/README.md](docs/maintainer/README.md)
- [MessageBus と Bridge の設計](docs/architecture/bridge-design.md)
- [コネクター詳細ガイド (DLL & サイドカー)](docs/guides/connectors-deep-dive.md)
- [Web API 対応状況](docs/maintainer/api-compatibility.md)

## ライセンス

- [LICENSE](LICENSE)
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)