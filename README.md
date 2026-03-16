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
  - [JavaScript API (GameBridge)](#javascript-apiwindowgamebridge)
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
2. **依存パッケージの取得**
   ```powershell
   dotnet restore src\WebView2AppHost.csproj
   ```
3. **ビルド**
   ```powershell
   msbuild src\WebView2AppHost.csproj /p:Configuration=Release /p:Platform=x64
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
python scripts\pack_zip.py my-app-content app.zip

# 2. EXE と ZIP を同じ名前にして同じフォルダに配置
copy src\bin\x64\Release\net472\WebView2AppHost.exe dist\MyApp.exe
move app.zip dist\MyApp.zip
```

#### C. 連結
EXE と ZIP を 1 ファイルにまとめます。
```powershell
# 1. 任意のフォルダ (例: my-app-content/) を ZIP 化
python scripts\pack_zip.py my-app-content app.zip

# 2. EXE の末尾に ZIP を貼り付けた「連結済み EXE」を生成
python scripts\bundle.py src\bin\x64\Release\net472\WebView2AppHost.exe app.zip dist\MyApp.exe
```

### 頒布に必要なファイル

最小構成（連結方式でも DLL 類は必要です）:
```
MyApp.exe
MyApp.exe.config
Microsoft.Web.WebView2.Core.dll
Microsoft.Web.WebView2.WinForms.dll
WebView2Loader.dll
LICENSE
THIRD_PARTY_NOTICES.md
```

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

### JavaScript API (`window.GameBridge`)

JS から呼び出せるネイティブ制御 API です。

```js
GameBridge.exitApp();           // アプリ終了
GameBridge.toggleFullscreen();  // フルスクリーン切替
await GameBridge.isFullscreen(); // 状態取得 (Promise<bool>)
```

**ライフサイクルイベント:**
- `appClosing`: ウィンドウを閉じる直前に発火。`GameBridge.confirmClose()` 等で制御。
- `visibilitychange`: 最小化や非アクティブ化を検知。

### 現在の制限事項

- **通知 (Notification API)**: UI 実装がないため、デフォルトでは表示されません。
- **新しいウィンドウ**: `window.open()` 等によるポップアップは制限されます。
- **カスタムスキーム**: `https://app.local/` で動作するため、一部の Service Worker 等で追加設定が必要になる場合があります。

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
App.cs の settings.AreDevToolsEnabled を true に変更してください。
</details>

### ライセンス

本アプリケーションは [MIT ライセンス](LICENSE) で配布されています。  
サードパーティ製コンポーネントのライセンスは [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。
