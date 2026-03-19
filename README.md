# WebView2 App Host

WebView2 を使って、HTML / CSS / JavaScript で作った Web アプリを Windows デスクトップアプリとして配布するための軽量ホストです。

- `https://app.local/` で完全ローカル配信
- `window.close()`、`requestFullscreen()` などの標準 Web API と自然に連携
- Web コンテンツは `www/`、ZIP、EXE 連結、埋め込みリソースのいずれでも扱える
- 配布物の作成方法が複数あり、いずれの方法でも小ささと速さを維持しつつ、用途に応じて運用を柔軟に構成できる

![screenshot](images/screenshot.png)

---

## 目次

1. [概要](#概要)
2. [主な特徴](#主な特徴)
3. [比較](#比較)
4. [必要な環境](#必要な環境)
5. [クイックスタート](#クイックスタート)
6. [Web コンテンツの差し替え](#web-コンテンツの差し替え)
7. [配布方法](#配布方法)
8. [設定](#設定)
9. [Web API との連携](#web-api-との連携)
10. [制限事項](#制限事項)
11. [ショートカットキー](#ショートカットキー)
12. [FAQ](#faq)
13. [プロジェクト構成](#プロジェクト構成)
14. [開発メモ](#開発メモ)
15. [ライセンス](#ライセンス)

---

## 概要

このリポジトリは、WebView2 をベースにした Windows 向けの Web アプリホストです。既存の Web アプリ資産をほぼそのまま使い、デスクトップ向け EXE として配布することを目的にしています。

想定している用途は次のとおりです。

- 既存の Web アプリを Windows デスクトップ向けに包みたい
- インストール不要に近い形で配布したい
- Web アプリとネイティブ機能の橋渡しを最小限で実装したい
- 単一ファイル、同梱 ZIP、外部 `www/` など複数の配布形態を使い分けたい

---

## 主な特徴

### 1. 配布物が小さい
本ホストの主な狙いは、Web アプリのローカルアプリ化に伴う配布サイズを抑えることです。Electron のように Chromium と Node.js をアプリに同梱する方式と比べると、配布物を小さく保ちやすい構成です。

### 2. 配布方法を複数選べる
Web コンテンツは、用途に応じて次の形で扱えます。

- `www/` フォルダを EXE 隣接で配置
- EXE と同名の ZIP ファイルを同じ場所に置く
- copy /b コマンド等で EXE の末尾に ZIP を連結する
- ビルド時にリソースとして EXE に埋め込む

### 3. 標準 Web API との親和性
次のような Web 標準 API が、ホスト側の追加実装に依存せずに自然に使えます。

- `window.close()` によるアプリ終了
- `element.requestFullscreen()` / `document.exitFullscreen()` によるフルスクリーン制御
- `beforeunload` による終了確認
- `target="_blank"` や `window.open()` による外部リンク起動

### 4. 生成コストを抑えやすい
本ホストは、通常の .NET ビルドに加えて、既存の実行ファイルへ ZIP を連結する方法や、ZIP / ローカルファイルをそのまま参照する方法を持っています。用途によっては、配布物の再生成にほぼ時間をかけずに運用できます。

---

## 比較

以下は、同一環境で各ターゲットのデフォルトコンテンツを用いて測定した実測値です。

| 対象 | ビルド時間 | サイズ | 備考 |
|---|---:|---:|---|
| Electron | 12,909 ms | 343.56 MB | zip 化後 132 MB |
| Tauri（初回ビルド） | 391,344 ms | 1.80 MB | installer サイズ |
| Tauri（再実行ビルド） | 126,863 ms | 1.80 MB | installer サイズ |
| Neutralino.js | 1,399 ms | 2.53 MB | zip 化後 920 KB |
| WebView2Host ビルド | 5,197 ms | 852 KB | zip 化後 245 KB |
| WebView2Host zip 連結 | 143 ms | 856 KB | zip 化後 245 KB |
| WebView2Host zip 同封 | 0 ms | 856 KB | zip 化後 249 KB |
| WebView2Host 個別配置 | 0 ms | 872 KB | zip 化後 253 KB |

> 本ホストの特徴は、単に配布物を小さくできることだけではなく、配布物を作るための手順を「通常ビルド」「再パッケージ」「ゼロ生成」として、単独でも組み合わせても使える点にあります。

## 必要な環境

### 開発環境
- Windows 10 以降
- Visual Studio 2022（.NET デスクトップ開発ワークロード）

### 実行環境
- Windows 10 以降
- .NET Framework 4.7.2
- WebView2 ランタイム

---

## クイックスタート

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

## Web コンテンツの差し替え

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

## 配布方法

このプロジェクトは、用途に応じてファイルごとに複数の配置方式を使い分けられます。

### コンテンツの読み込み優先順
同名ファイルが複数の場所に存在する場合は、上位のソースが優先されます。

1. `www/` フォルダ（EXE 隣接）
2. 起動引数で渡された ZIP パス
3. EXE と同名の ZIP（例: `MyApp.exe` に対して `MyApp.zip`）
4. EXE に連結した ZIP
5. EXE 内部に埋め込まれたリソース

### 1. `www/` フォルダ
EXE と同じ場所に `www/` フォルダを置き、そこにコンテンツを配置します。

向いている用途:
- 開発中の即時反映
- 頻繁に更新するアセット
- 動画・音声など、Range Request が必要な大きなファイル

### 2. ZIP 同梱
EXE と同じ名前の ZIP を用意し、隣に置きます。

向いている用途:
- コンテンツだけを差し替える配布
- プラグインや MOD 的な拡張

### 3. EXE 連結
copy /b コマンド等を使って、ZIP を EXE の末尾に物理結合します。

向いている用途:
- 単一ファイルに近い形で配布したい
- ビルド後の配布物を簡素化したい

```powershell
cmd /c copy /b src\bin\x64\Release\net472\WebView2AppHost.exe + src\app.zip dist\MyApp.exe
```

### 4. 埋め込みリソース
ビルド時に `web-content/` を EXE 内部へ埋め込みます。

向いている用途:
- 最小構成で配布したい
- コアとなるファイルを外出ししたくない

---

## 設定

### `app.conf.json`

`web-content/app.conf.json`、または各 ZIP / フォルダのルートに配置して設定します。

| キー | 型 | デフォルト | 説明 |
|---|---:|---:|---|
| `title` | string | `"WebView2 App Host"` | 起動時のウィンドウタイトル |
| `width` | int | `1280` | 初期幅（ピクセル） |
| `height` | int | `720` | 初期高さ（ピクセル） |
| `fullscreen` | bool | `false` | フルスクリーン起動するかどうか |

### 例

```json
{
  "title": "My App",
  "width": 1440,
  "height": 900,
  "fullscreen": false
}
```

---

## Web API との連携

このホストは、Web 標準 API を中心に操作できるように設計されています。

### アプリ終了
```js
window.close();
```
`window.close()` でアプリを終了できます。

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

## 制限事項

- Notification API は UI 実装がないため、デフォルトでは表示されません
- `https://app.local/` ベースのため、一部の Service Worker などでは追加設定が必要になる場合があります
- ZIP 内のファイルは Range Request に対応しません
- 動画・音声・WASM など、Range Request が必要なファイルは `www/` フォルダに個別配置してください

---

## ショートカットキー

| キー | 動作 |
|---|---|
| `F11` | フルスクリーン切り替え |
| `ESC` | フルスクリーン解除 |

---

## FAQ

### アイコンを変更したい
`resources/app.ico` を差し替えて再ビルドしてください。

### DevTools を開きたい
Debug ビルドでは自動的に有効です。Release ビルドで有効にしたい場合は、`src/App.cs` 内の `#if DEBUG` ブロックを調整してください。

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

### `www/` と ZIP のどちらを使うべきですか
開発中は `www/`、配布時は ZIP か埋め込みが扱いやすいです。大きなメディアを扱う場合は `www/` が適しています。
ファイルごとに適切な配置を選択できますので、どちらも同時に利用できます。

---

## プロジェクト構成

```text
.
├── .github/
│   └── workflows/
├── docs/
├── images/
├── resources/
├── src/
├── web-content/
├── LICENSE
├── README.md
└── THIRD_PARTY_NOTICES.md
```

---

## 開発メモ

- メイン実装は `src/` 配下にあります
- サンプルの Web コンテンツは `web-content/` にあります
- GitHub Actions によるビルド / リリースの自動化が含まれています

---

## ライセンス

本リポジトリは MIT ライセンスで配布されています。サードパーティ製コンポーネントのライセンスは `THIRD_PARTY_NOTICES.md` を参照してください。

