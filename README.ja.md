# WebView2 App Host

[![Build](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml/badge.svg)](https://github.com/hadamak/webview2-app-host-cs/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/hadamak/webview2-app-host-cs)](https://github.com/hadamak/webview2-app-host-cs/releases/latest)
[![License: MIT](https://img.shields.io/github/license/hadamak/webview2-app-host-cs)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078d4?logo=windows)](https://www.microsoft.com/windows)
[![.NET Framework](https://img.shields.io/badge/.NET_Framework-4.7.2-512bd4?logo=dotnet)](https://dotnet.microsoft.com)

WebView2 を使って、HTML / CSS / JavaScript で作った Web アプリを Windows デスクトップアプリとして配布するための軽量ホストです。

- 🌐 `https://app.local/` で完全ローカル配信
- 🔗 `window.close()`、`requestFullscreen()` などの標準 Web API と自然に連携
- 📦 Web コンテンツは `www/`、ZIP、EXE 連結、埋め込みリソースのいずれでも扱える
- 🚀 配布物の作成方法が複数あり、いずれの方法でも小ささと速さを維持しつつ、用途に応じて運用を柔軟に構成できる

![screenshot](images/screenshot.png)

---

## 📋 目次

1. [概要](#概要)
2. [主な特徴](#主な特徴)
3. [比較](#比較)
4. [利用例](#利用例)
5. [必要な環境](#必要な環境)
6. [クイックスタート](#クイックスタート)
7. [Web コンテンツの差し替え](#web-コンテンツの差し替え)
8. [配布方法](#配布方法)
9. [設定](#設定)
10. [Web API との連携](#web-api-との連携)
11. [Steam 連携](#steam-連携)
12. [制限事項](#制限事項)
13. [ショートカットキー](#ショートカットキー)
14. [FAQ](#faq)
15. [プロジェクト構成](#プロジェクト構成)
16. [開発メモ](#開発メモ)
17. [ライセンス](#ライセンス)

---

## 🔍 概要

このリポジトリは、WebView2 をベースにした Windows 向けの Web アプリホストです。既存の Web アプリ資産をほぼそのまま使い、デスクトップ向け EXE として配布することを目的にしています。

想定している用途は次のとおりです。

- 既存の Web アプリを Windows デスクトップ向けに包みたい
- インストール不要に近い形で配布したい
- EXE 連結・同梱 ZIP・外部 `www/` など複数の配布形態を使い分けたい
- ネットワークに置けないコンテンツを、ローカルサーバーなしに `https://` オリジンで動作させたい

---

## ✨ 主な特徴

### 1. 🛠️ ビルド環境が不要
リリース済みの EXE に Web コンテンツを添付するだけで、すぐに動作するアプリケーションになります。.NET SDK・Visual Studio・Node.js など、いかなるツールチェーンのインストールも不要です。

コンテンツの配置方法は用途に応じて選べます。

- `www/` フォルダを EXE と同じ場所に置く（即時反映・開発向け）
- EXE と同名の ZIP を隣に置く
- `copy /b` コマンドで EXE 末尾に ZIP を連結する（コンテンツを EXE にまとめる）
- ビルド時にリソースとして EXE に埋め込む

### 2. 📦 配布物が小さい
WebView2 ランタイムは Windows 10/11 に標準搭載されているため、Chromium を同梱する必要がありません。Electron と比べると配布サイズを大幅に抑えられます。

### 3. 🌐 標準 Web API との親和性
ブラウザ互換の動作環境を提供するため、コンテンツ側に特別な対応は必要ありません。次のような Web 標準 API がそのまま機能します。

- `window.close()` によるアプリ終了
- `element.requestFullscreen()` / `document.exitFullscreen()` によるフルスクリーン制御
- `beforeunload` による終了確認
- `target="_blank"` や `window.open()` による外部リンク起動
- `visibilityState` / `visibilitychange` によるウィンドウ最小化の検知

### 4. 🎮 Steam 連携
Steamworks SDK を意識せずに、Web ゲームを Steam で配布できます。Steam サポート ZIP を追加して HTML から Facepunch.Steamworks の API を利用するだけで連携が完結します。

対応している機能:

- 🏆 実績 — JavaScript から Steam 実績を解除
- 📊 User Stats — 数値データを Steam に保存・取得
- ☁️ Steam Cloud — セーブデータや設定を端末間で同期
- 🏅 Leaderboards — グローバルランキングへのスコア送信・取得
- 👥 Rich Presence — フレンド一覧にプレイ状況を表示
- 🎁 所有権 / DLC — 本編・DLC の所有状態確認

---

## 📊 比較

「ビルド環境が不要」「配布物が小さい」という特徴を他のフレームワークと比較した実測値です。

| 対象 | ビルド時間 | サイズ | 備考 |
|---|---:|---:|---|
| Electron | 12,909 ms | 343.56 MB | zip 化後 132 MB |
| Tauri（初回ビルド） | 391,344 ms | 1.80 MB | installer サイズ |
| Tauri（再実行ビルド） | 126,863 ms | 1.80 MB | installer サイズ |
| Neutralino.js | 1,399 ms | 2.53 MB | zip 化後 920 KB |
| WebView2Host ビルド | 6,197 ms | 884 KB | zip 化後 254 KB |
| WebView2Host zip 連結 | 74 ms | 888 KB | zip 化後 254 KB |
| WebView2Host zip 同封 | 0 ms | 888 KB | zip 化後 258 KB |
| WebView2Host 個別配置 | 0 ms | 895 KB | zip 化後 258 KB |

> ⚡ zip 連結・個別配置のように、ビルド済み EXE をそのまま使う運用ではビルド時間がゼロになります。これが「ビルド環境が不要」という特徴の実態です。

---

## 💡 利用例

### 🕹️ ファンタジーコンソール
JS 製ゲームエンジンを EXE に内包し、ZIP をゲームカートリッジとして扱えます。EXE アイコンに ZIP をドラッグ＆ドロップすると起動引数として渡され、そのコンテンツが読み込まれます。カートリッジを差し替えるという物理的な操作感がレトロなコンソール体験のフレーバーにもなります。

### 📚 オフラインドキュメントビューア
製品マニュアルや社内ドキュメントを ZIP として配布し、ネットワーク接続なしに閲覧できるビューアを作れます。ドキュメントの更新は ZIP の差し替えだけで完結し、閲覧ツール側の再配布は不要です。

### 🎪 インタラクティブな配布物
インストーラや説明動画の代わりに、HTML で作ったインタラクティブなコンテンツを EXE として渡せます。製品デモ・チュートリアル・展示会向けのプレゼンテーションなど、受け取った相手が何もインストールせずに開ける形で配布できます。

---

## 💻 必要な環境

### 開発環境
- Windows 10 以降
- Visual Studio 2022（.NET デスクトップ開発ワークロード）

### 実行環境
- Windows 10 以降
- .NET Framework 4.7.2
- WebView2 ランタイム

---

## 🚀 クイックスタート

### 1. リポジトリを取得

```bash
git clone https://github.com/hadamak/webview2-app-host-cs.git
cd webview2-app-host-cs
```

### 2. ビルド

```bat
msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64
```

### 3. 起動

ビルド後に生成された EXE を実行します。初回はサンプルコンテンツが読み込まれます。

---

## 🔄 Web コンテンツの差し替え

このホストは `web-content/` をアプリ本体のコンテンツ領域として扱います。中身を自分の Web アプリに置き換えて再ビルドするだけで利用できます。

### 基本ルール
- `index.html` はエントリポイントです
- `web-content/` 以下のファイルはビルド時に `app.zip` としてまとめられます
- 生成されたコンテンツは EXE に埋め込まれます

### 最小構成の例

```text
web-content/
├── index.html
├── app.conf.json
├── assets/
│   └── ...
└── scripts/
    └── ...
```

### 置き換えの手順
1. `web-content/` の中身を自分のアプリファイルに差し替える
2. 必要に応じて `app.conf.json` を編集する
3. 再ビルドする
4. 生成物を起動して確認する

---

## 📦 配布方法

このプロジェクトは、用途に応じてファイルごとに複数の配置方式を使い分けられます。

### コンテンツの読み込み優先順
同名ファイルが複数の場所に存在する場合は、上位のソースが優先されます。

1. `www/` フォルダ（EXE 隣接）
2. 起動引数で渡された ZIP パス
3. EXE と同名の ZIP（例: `MyApp.exe` に対して `MyApp.zip`）
4. EXE に連結した ZIP
5. EXE 内部に埋め込まれたリソース

### 1. 📁 `www/` フォルダ
EXE と同じ場所に `www/` フォルダを置き、そこにコンテンツを配置します。

向いている用途:
- 開発中の即時反映
- 頻繁に更新するアセット
- 動画・音声など、Range Request が必要な大きなファイル

### 2. 🗜️ ZIP 同梱
EXE と同じ名前の ZIP を用意し、隣に置きます。

向いている用途:
- コンテンツだけを差し替える配布
- プラグインや MOD 的な拡張

### 3. 🔗 EXE 連結
copy /b コマンド等を使って、ZIP を EXE の末尾に物理結合します。

向いている用途:
- コンテンツを EXE にまとめて配布物のファイル数を減らしたい
- ビルド後の配布物を簡素化したい

```powershell
cmd /c copy /b src\bin\x64\Release\net472\WebView2AppHost.exe + src\app.zip src\bin\x64\Release\net472\MyApp.exe
```

### 4. 🧱 埋め込みリソース
ビルド時に `web-content/` を EXE 内部へ埋め込みます。

向いている用途:
- 最小構成で配布したい
- コアとなるファイルを外出ししたくない

---

## ⚙️ 設定

### `app.conf.json`

`web-content/app.conf.json`、または各 ZIP / フォルダのルートに配置して設定します。コンテンツ制作者向けの設定です。

| キー | 型 | デフォルト | 説明 |
|---|---:|---:|---|
| `title` | string | `"WebView2 App Host"` | 起動時のウィンドウタイトルの初期値 |
| `width` | int | `1280` | 初期幅（ピクセル） |
| `height` | int | `720` | 初期高さ（ピクセル） |
| `fullscreen` | bool | `false` | フルスクリーン起動するかどうか |
| `proxyOrigins` | string[] | `[]` | CORS プロキシを許可する外部オリジンのリスト |

### CORS プロキシ

`proxyOrigins` に許可オリジンを列挙すると、そのオリジンへのリクエストをホストが透過的に転送します。コンテンツ側は通常の URL で `fetch` でき、ブラウザでそのまま開いた場合は通常の CORS が適用されます。

```json
{ "proxyOrigins": ["https://api.example.com"] }
```

```js
// コンテンツ側 — ホスト固有の記述は不要
const res = await fetch('https://api.example.com/v1/data');
```

許可リストにないオリジンへのリクエストは通常通り WebView2 が処理します（CORS エラーになる場合があります）。

> ℹ️ **実装メモ**: プロキシは CDP（Chrome DevTools Protocol）の Fetch ドメインを使って実装されています。
> リクエストをネットワークスタックより手前でインターセプトするため、
> GET に加え POST / PUT / DELETE などのメソッドとリクエストボディ（JSON・フォームデータ等）も転送できます。
>
> `multipart/form-data` によるバイナリファイルアップロードは完全にはサポートされていません。
> その場合はサーバー側で CORS を設定することを検討してください。

### `user.conf.json`

EXE と同じ場所に置くことで、エンドユーザーがウィンドウの表示設定を上書きできます。
`app.conf.json` の値より優先されます。省略したフィールドは `app.conf.json` の値がそのまま使われます。

| キー | 型 | 説明 |
|---|---:|---|
| `width` | int | 初期幅（ピクセル） |
| `height` | int | 初期高さ（ピクセル） |
| `fullscreen` | bool | フルスクリーン起動するかどうか |

### 例

```json
{
  "title": "My App",
  "width": 1440,
  "height": 900,
  "fullscreen": false
}
```

### HTML 側からの追従

起動時のウィンドウタイトルは `app.conf.json` の `title` を初期値として使います。その後はページの `<title>` が変わるたびにウィンドウタイトルへ反映され、favicon が設定されている場合はウィンドウアイコンにも反映されます。

---

## 🔗 Web API との連携

このホストは、Web 標準 API を中心に操作できるように設計されています。

### アプリ終了
```js
window.close();
```
`window.close()` でアプリを終了できます。ただし、ブラウザの仕様に従い、ユーザー操作によるページ遷移が発生していない状態（ホストが直接ナビゲートしたページ）でのみ有効です。

### フルスクリーン
```js
element.requestFullscreen();
document.exitFullscreen();
```
呼び出しに応じて、ホスト側のウィンドウ状態も連動します。

### 終了確認
```js
window.addEventListener('beforeunload', (event) => {
  event.preventDefault();
  event.returnValue = '';
});
```
保存確認などを、ブラウザ標準の作法で実装できます。

### 外部リンク
`target="_blank"` や `window.open()` で開かれた `http(s)` リンクは、OS の既定ブラウザで開かれます。

### ライフサイクル
- `visibilitychange`
- `fullscreenchange`

ウィンドウの最小化やフルスクリーン状態の変化を検知できます。

### `window.AppBridge`
現在は提供していません。ホスト固有 API を増やさず、標準 Web API を中心に使う方針です。

---

## 🎮 Steam 連携

Steamworks 連携はオプションです。ホスト本体の基本方針は標準 Web API 中心ですが、Steam 連携は Facepunch.Steamworks の API を JS から利用します。

そのため、本 README では詳細を展開せず、対象別の別文書にまとめています。

- 📖 アプリ開発者向け入口: `docs/steam/overview.md`
- ⚡ 最短導入手順: `docs/steam/getting-started.md`
- 🎯 付属サンプル: `samples/steam-complete/`

通常配布物と Steam 関連配布物は分離する想定です。Steam 対応が必要な場合のみ、別配布の Steam サポート一式を追加してください。

---

## ⚠️ 制限事項

- `https://app.local/` ベースのため、一部の Service Worker などでは追加設定が必要になる場合があります
- ZIP 内の大きなファイル（動画・音声等）はリクエスト時にメモリに展開されます。メモリを圧迫する場合は `www/` フォルダへの個別配置を検討してください

---

## ⌨️ ショートカットキー

フルスクリーンの切り替えは `requestFullscreen()` / `exitFullscreen()` で制御します。

### ブラウザ組み込みキーの扱い

ホストはブラウザと同様の動作を提供するため、F5 リロード・Ctrl+F・Ctrl+P 等のショートカットキーはデフォルトで有効です。
コンテンツ側で特定のキーを抑制したい場合は、ブラウザと同様に JS の `keydown` イベントで処理できます。

```js
window.addEventListener('keydown', (e) => {
    // F5 / Ctrl+R によるリロードを抑制する例
    if (e.key === 'F5' || (e.ctrlKey && e.key === 'r')) {
        e.preventDefault();
    }
});
```

---

## ❓ FAQ

### アイコンを変更したい
`resources/app.ico` を差し替えて再ビルドしてください。あわせて、HTML 側で favicon を設定すると、起動後のウィンドウアイコンもその内容に追従します。

### DevTools を開きたい
Debug ビルドでは自動的に有効です。Release ビルドで有効にしたい場合は、`src/App.cs` 内の `#if DEBUG` ブロックを調整してください。

### Steamworks を使いたい
本体 README では概要のみに留めています。アプリ開発者は `docs/steam/overview.md` から読み始め、必要に応じて `docs/steam/getting-started.md` と `docs/steam/api-reference.md` を参照してください。ブリッジビルド担当向けには `docs/steam/bridge-build.md` を用意しています。

### どのファイルを配布すればよいですか
配布方式によって異なりますが、基本的には次が必要です。

- `WebView2AppHost.exe`
- コンテンツ（埋め込み / 同封 / 連結 / `www/` のいずれか）
- `Microsoft.Web.WebView2.Core.dll`
- `Microsoft.Web.WebView2.WinForms.dll`
- `WebView2Loader.dll`
- `WebView2AppHost.exe.config`
- `LICENSE`
- `THIRD_PARTY_NOTICES.md`

`docs/README_TEMPLATE.txt` に配布時に便利な README テンプレートがあります。

### `www/` と ZIP のどちらを使うべきですか
開発中は `www/`、配布時は ZIP か埋め込みが扱いやすいです。大きなメディアを扱う場合は `www/` が適しています。
ファイルごとに適切な配置を選択できますので、どちらも同時に利用できます。

---

## 🗂️ プロジェクト構成

```text
.
├── .github/
│   └── workflows/          # CI/CD（ビルド・リリース）
├── docs/
│   └── steam/
│       ├── en/
│       │   ├── feature-guides/   # 実績・Stats・Cloud・Leaderboards など
│       │   ├── overview.md
│       │   ├── getting-started.md
│       │   └── api-reference.md
│       ├── feature-guides/       # 同内容の日本語版
│       ├── overview.md
│       ├── getting-started.md
│       └── api-reference.md
├── images/                 # README 用スクリーンショット
├── resources/
│   └── app.ico
├── samples/
│   └── steam-complete/     # Steam 連携の完全なサンプル
├── src/                    # ホストアプリ（Steam 依存なし）
│   ├── App.cs
│   ├── AppConfig.cs
│   ├── SteamBridge.cs      # プラグインローダー（リフレクションで Steam DLL を読み込み）
│   ├── ISteamBridgeImpl.cs # Steam DLL とのインターフェース契約
│   ├── WebResourceHandler.cs
│   ├── WebView2AppHost.csproj
│   └── ...
├── src-steam/              # Steam DLL プロジェクト（個別ビルド）
│   ├── SteamBridgeImpl.cs
│   └── WebView2AppHost.Steam.csproj
├── steam-support/          # 配布用ビルド済み Steam サポート ZIP
├── test-www/               # 開発・動作確認用 Web コンテンツ
├── tests/
│   ├── HostTests/          # C# ホストのテスト
│   └── steam-js/           # steam.js の JavaScript テスト
├── tools/
│   └── package-steam-support.ps1
├── web-content/            # デフォルトの埋め込み Web コンテンツ
│   ├── index.html
│   ├── steam.js
│   └── app.conf.json
├── Directory.Packages.props
├── LICENSE
├── README.md
├── README.ja.md
└── THIRD_PARTY_NOTICES.md
```

---

## 🔨 開発メモ

- メイン実装は `src/` 配下にあります
- サンプルの Web コンテンツは `web-content/` にあります
- GitHub Actions によるビルド / リリースの自動化が含まれています

---

## 📄 ライセンス

本リポジトリは MIT ライセンスで配布されています。サードパーティ製コンポーネントのライセンスは `THIRD_PARTY_NOTICES.md` を参照してください。
