# WebView2 App Host

Windows 向け Web アプリホスト。HTML / CSS / JS で作られた Web アプリを Windows デスクトップアプリとして配布するための軽量テンプレートです。

---

## 目次

- [**1. 概要と特徴**](#1-概要と特徴)
- [**2. セットアップとビルド**](#2-セットアップとビルド)
  - [必要な環境](#必要な環境)
  - [ローカルビルド手順](#ローカルビルド)
  - [Web コンテンツの差し替え](#web-コンテンツの差し替え)
- [**3. コンテンツの配信と配布**](#3-コンテンツの配信と配布)
  - [コンテンツの読み込み順序](#コンテンツの読み込み順序)
  - [配布形態の選択（同封・連結・埋め込み）](#配布形態の選択)
  - [頒布に必要なファイル構成](#頒布に必要なファイル)
- [**4. 開発リファレンス**](#4-開発リファレンス)
  - [ウィンドウ設定 (app.conf.json)](#ウィンドウ設定appconfjson)
  - [JavaScript API (AppBridge)](#javascript-apiwindowappbridge)
  - [現在の制限事項](#現在の制限事項)
- [**5. その他**](#5-その他)
  - [キーボードショートカット](#キーボードショートカット)
  - [よくある質問](#よくある質問)
  - [ライセンス](#ライセンス)

---

## 1. 概要と特徴

WebView2（Chromium ベース）を利用し、ES Modules・WebAssembly・Web Audio API などの最新 Web 技術をネイティブアプリ環境で提供します。

| カテゴリ | 特徴 |
|:---:|---|
| **配信** | 仮想ホスト `https://app.local/` による完全ローカル配信、Range Request (シーク) 対応 |
| **パッキング** | 同封、連結、埋め込みリソースなど、柔軟な配布形態を選択可能 |
| **ネイティブ連携** | JS からの終了・フルスクリーン制御、タイトル・favicon の自動追従 |
| **軽量・高速** | 外部 DLL 依存なし（WebView2 除く）、頒布最小構成 約 850KB |

---

## 2. セットアップとビルド

### 必要な環境

**開発環境**
- Windows 10 以降
- Visual Studio 2022（.NET デスクトップ開発ワークロード）
- Python 3.8 以上

**実行環境**
- Windows 10 以降
- .NET Framework 4.7.2（標準搭載）
- WebView2 ランタイム（標準搭載、または要インストール）

### ローカルビルド

1. **リポジトリのクローン**
   ```powershell
   git clone https://github.com/hadamak/webview2-app-host-cs.git
   cd webview2-app-host-cs
   ```
2. **ビルド**（NuGet パッケージの取得とコンパイルを一括実行）
   ```powershell
   msbuild src\WebView2AppHost.csproj "/t:Restore;Build" /p:Configuration=Release /p:Platform=x64
   ```

### Web コンテンツの差し替え

`web-content/` の中身を自分のアプリファイルに入れ替えて再ビルドするだけです。
- `index.html` がエントリポイントです（必須）。
- ビルド時に `web-content/` 以下がすべて `app.zip` として固められ、EXE に埋め込まれます。

---

## 3. コンテンツの配信と配布

### コンテンツの読み込み順序

アプリ起動時、以下の優先順位でコンテンツを検索します。同名ファイルが存在する場合、上位のソースが優先されます。

| 優先度 | 名称 | 配置・指定方法 | 主な用途 |
|:---:|---|---|---|
| **1** | **個別配置** | `www/` フォルダ（EXE 隣接） | 開発中の即時反映、大容量アセットの外部化 |
| **2** | **外部指定** | 起動引数で ZIP パスを渡す | テスト・デバッグ、複数コンテンツの切り替え |
| **3** | **同封** | `{EXE名}.zip`（EXE 隣接） | コンテンツのみのアップデート配布 |
| **4** | **連結** | `bundle.py` で EXE 末尾に結合 | ポータビリティ重視（単一ファイル化） |
| **5** | **埋め込み** | リソースとして EXE 内部に内蔵 | 最小構成、改ざん防止 |

### 配布形態の選択

#### A. 埋め込み（標準）
追加作業なし。`web-content/` が EXE 内部にパッケージングされます。

#### B. 同封
EXE とは別に ZIP を用意。コンテンツのみの更新が容易です。
```powershell
# 1. 任意のフォルダ (例: my-app-content/) を ZIP 化
python scripts\make_zip.py my-app-content app.zip

# 2. EXE と ZIP を同じ名前にして同じフォルダに配置
copy bin\WebView2AppHost.exe dist\MyApp.exe
move app.zip dist\MyApp.zip
```

#### C. 連結
EXE と ZIP を 1 ファイルにまとめます。
```powershell
# 1. 任意のフォルダ (例: my-app-content/) を ZIP 化
python scripts\make_zip.py my-app-content app.zip

# 2. EXE の末尾に ZIP を貼り付けた「連結済み EXE」を生成
python scripts\bundle.py bin\WebView2AppHost.exe app.zip dist\MyApp.exe
```

### ダウンロードパッケージの内容

自動リリース（GitHub Actions）で生成される `WebView2AppHost-v*-win-x64.zip` の内部構成は以下の通りです。

| ファイル / フォルダ | 内容 |
|:---|:---|
| **`WebView2AppHost.exe`** | ホスト本体 |
| **`*.dll` / `*.exe.config`** | 実行に必要な WebView2 SDK ファイル群 |
| **`www/`** | デモコンテンツ。AppBridge API の利用例として参照できます。起動時に自動的に読み込まれます。 |
| **`scripts/bundle.py`** | EXE 末尾に ZIP を結合して単一ファイル化するスクリプトです。 |
| **`LICENSE`** / **`THIRD_PARTY_NOTICES.md`** | 本アプリおよびサードパーティのライセンス通知です。 |

**自分のコンテンツで動かすには:**
1. ZIP を展開して任意の場所に置く。
2. `WebView2AppHost.exe` と同じ場所に `www/` フォルダを作成し、自分のコンテンツを配置する（同梱の `www/` は上書きまたは削除）。
3. `WebView2AppHost.exe` を実行する。

### エンドユーザーへの頒布に必要なファイル

ホストアプリケーションにコンテンツを組み込んでエンドユーザーに配布する場合、以下のファイルが必要です。

| ファイル | 内容 |
|:---|:---|
| **`WebView2AppHost.exe`**（任意の名称にリネーム可） | ホスト本体 |
| **コンテンツ**（埋め込み・同封・連結のいずれか） | 自作の Web アプリ |
| **`Microsoft.Web.WebView2.Core.dll`** | WebView2 SDK（必須） |
| **`Microsoft.Web.WebView2.WinForms.dll`** | WebView2 SDK（必須） |
| **`WebView2Loader.dll`** | WebView2 SDK（必須） |
| **`WebView2AppHost.exe.config`** | 実行構成ファイル（必須） |
| **`LICENSE`** / **`THIRD_PARTY_NOTICES.md`** | ライセンス通知（同梱義務あり） |

> [!NOTE]
> DLL 類は EXE と同じ場所に置いてください。

---

## 4. 開発リファレンス

### ウィンドウ設定 (app.conf.json)

`web-content/app.conf.json`（または各 ZIP/フォルダのルート）で設定します。

| キー | 型 | デフォルト | 説明 |
|---|---|---|---|
| `title` | string | `"WebView2 App Host"` | 起動時のタイトル |
| `width` | int | `1280` | 初期幅（ピクセル） |
| `height` | int | `720` | 初期高さ（ピクセル） |
| `fullscreen` | bool | `false` | フルスクリーン起動 |

### 標準 Web API への対応
本ホストアプリは WebView2 の機能を活用し、Web 標準のライフサイクルに準拠しています。以下の標準 API が特別な設定なしにそのまま利用可能です。

- **アプリ終了**: `window.close()`
  - JavaScript から呼び出すことでアプリを終了します。
- **フルスクリーン**: `element.requestFullscreen()` / `document.exitFullscreen()`
  - 呼び出しに合わせてホスト側のウィンドウ状態（ボーダレス・最大化）が自動的に連動します。
- **終了確認**: `beforeunload` イベント
  - 保存確認ダイアログなどをブラウザ標準の作法で実装できます。
- **外部リンク**: `target="_blank"` / `window.open()`
  - `app.local` 以外の `http(s)` リンクは、自動的に OS 既定のブラウザで開かれます。
- **ライフサイクル**: `visibilitychange` / `fullscreenchange`
  - ウィンドウの最小化やフルスクリーン状態の変化を検知できます。

### JavaScript API (`window.AppBridge`)
Web 標準でカバーできないネイティブ固有の機能を最小限提供します。

- 現在、提供されている API はありません（すべての制御が標準 Web API へ移行されました）。

### 現在の制限事項

- **通知 (Notification API)**: UI 実装がないため、デフォルトでは表示されません。
- **カスタムスキーム**: `https://app.local/` で動作するため、一部の Service Worker 等で追加設定が必要になる場合があります。
- **Range Request (シーク)**: ZIP に格納されたファイルは Range Request に対応しません。動画・音声・wasm など Range Request が必要なファイルは `www/` フォルダへの個別配置を使用してください。

---

## 5. その他

### キーボードショートカット

| キー | 動作 |
|------|------|
| F11 | フルスクリーン切替 |
| ESC | フルスクリーン解除 |

### よくある質問

<details>
<summary>Q. アイコンを変えるには？</summary>
resources/app.ico を差し替えてリビルドしてください。
</details>

<details>
<summary>Q. 開発中に DevTools を開くには？</summary>
Debug ビルドでは自動的に有効になります。Release ビルドで有効にしたい場合は App.cs の <code>#if DEBUG</code> ブロックを調整してください。
</details>

### ライセンス

本アプリケーションは [MIT ライセンス](LICENSE) で配布されています。  
サードパーティ製コンポーネントのライセンスは [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。
