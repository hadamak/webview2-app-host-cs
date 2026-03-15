# WebView2 App Host

Windows 向け Web アプリホスト。ゲーム・ツール・ビジュアライザーなど、ブラウザで動く Web アプリ（HTML / CSS / JS）を Windows デスクトップアプリとして配布するためのテンプレートリポジトリです。

WebView2（Windows 標準搭載の Chromium ベース WebView）で動作するため、ES Modules・WebAssembly・Web Audio API など本番環境と同じ条件で実行できます。

## 特徴

| 機能 | 詳細 |
|------|------|
| **オフライン動作** | 仮想ホスト `https://app.local/` で完全ローカル配信 |
| **EXE 埋め込み配布** | `web-content/` 以下を EXE に埋め込み、インストール不要で配布できる |
| **外部コンテンツ配置** | EXE 隣の `www/` フォルダを `https://app.local/` にマップ（動画など大容量アセットに） |
| **柔軟な読み込み順序** | `www/` → 隣接 ZIP → WZGM 結合 → 埋め込み の優先順で自動検索 |
| **リクエスト時展開** | 起動時に全展開せず、リクエストのあったエントリだけ展開して返す |
| **無圧縮エントリ直読み** | ZIP_STORED エントリはメモリ展開なしで Range Request 対応 |
| **Range Request 対応** | 動画シークなど部分取得が必要なコンテンツに対応 |
| **JS → ネイティブ** | `window.GameBridge` で終了・フルスクリーン制御 |
| **appClosing** | × ボタン・Alt+F4 を JS で検知・キャンセル可能 |
| **visibilitychange** | 最小化・切替時に `document.visibilityState` が更新される |
| **フルスクリーン** | F11 / ESC / JS API で制御可能 |
| **タイトル自動追従** | `<title>` タグの変化をウィンドウタイトルに反映 |
| **favicon 自動追従** | `<link rel="icon">` の変化をウィンドウアイコンに反映 |
| **ウィンドウ設定** | `app.conf.json` でサイズ・タイトル・フルスクリーン起動を設定 |
| **localStorage 永続化** | EXE 名からユーザーデータディレクトリを自動決定 |
| **軽量・高速** | 依存取得 約3秒・ビルド 約5秒・頒布最小構成 849KB |

---

## 必要な環境

**開発環境**
- Windows 10 以降
- Visual Studio 2022（.NET デスクトップ開発ワークロード）
- Python 3.8 以上

**実行環境**
- Windows 10 以降
- .NET Framework 4.7.2（Windows 10 / 11 に標準搭載）
- WebView2 ランタイム（Windows 11 および最新の Windows 10 に標準搭載）

---

## ディレクトリ構成

```
webview2-app-host-cs/
│
├── src/
│   ├── WebView2AppHost.csproj  # プロジェクトファイル（NuGet・ビルド設定）
│   ├── Program.cs              # エントリポイント
│   ├── App.cs                  # メインウィンドウ・WebView2・JS ブリッジ
│   ├── ZipContentProvider.cs   # コンテンツ提供（ZIP・ディレクトリ）
│   ├── AppConfig.cs            # app.conf.json パース
│   ├── MimeTypes.cs            # 拡張子 → MIME タイプ
│   └── app.manifest            # DPI 設定マニフェスト
│
├── resources/
│   └── app.ico                 # EXE ファイルアイコン（ダミー同梱済み・差し替え推奨）
│
├── web-content/                # ← ここに Web コンテンツを置く（EXE に埋め込まれる）
│   ├── index.html              #   エントリポイント（必須）
│   ├── app.conf.json           #   ウィンドウ設定（省略可・デフォルト値あり）
│   └── assets/                 #   フォルダ構成は自由
│       ├── css/style.css
│       └── js/main.js
│
└── scripts/
    ├── pack_zip.py             # web-content/ を ZIP 化（MSBuild から自動呼び出し）
    └── bundle.py               # EXE 末尾に ZIP を結合して配布 EXE を生成
```

---

## ビルド手順

### 1. リポジトリのクローン

```powershell
git clone https://github.com/hadamak/webview2-app-host-cs.git
cd webview2-app-host-cs
```

### 2. 依存パッケージの取得（初回のみ）

```powershell
dotnet restore src\WebView2AppHost.csproj
```

NuGet パッケージ（WebView2 SDK）を取得します。`project.assets.json` が生成されれば完了です。

### 3. ビルド

```powershell
msbuild src\WebView2AppHost.csproj /p:Configuration=Release /p:Platform=x64
```

`web-content/` の ZIP 化（`src/app.zip` の生成）は MSBuild が自動で行います。

### 4. 実行

```powershell
.\src\bin\x64\Release\net472\WebView2AppHost.exe
```

---

## Web コンテンツの差し替え

`web-content/` の中身を自分のアプリファイルに入れ替えて再ビルドするだけです。

- `index.html` がエントリポイントです（必須）
- フォルダ構成は自由です。`assets/` 以外の名前でも構いません
- ビルド時に `web-content/` 以下がすべて ZIP 化されて EXE に埋め込まれます
- テキスト系（HTML/CSS/JS/JSON）は圧縮、動画・音声・画像などは無圧縮で格納されます

---

## コンテンツの読み込み順序

アプリ起動時、以下の順序でコンテンツを検索します。同名ファイルは上位ソースが優先されます。

```
1. www/ ディレクトリ（EXE と同じフォルダ）
      www/index.html       → https://app.local/index.html
      www/assets/video.mp4 → https://app.local/assets/video.mp4

2. メイン ZIP（以下のフォールバック順で最初に見つかったもの）
   2a. コマンドライン引数で指定された ZIP ファイル
         WebView2AppHost.exe C:\path\to\myapp.zip
   2b. EXE と同名の .zip ファイル（隣接ファイル）
         WebView2AppHost.exe
         WebView2AppHost.zip
   2c. EXE 末尾に結合された ZIP（bundle.py で生成した配布 EXE）
         MyApp.exe  ← EXE + ZIP + トレーラーを 1 ファイルに連結したもの
   2d. EXE に埋め込まれたリソース（デフォルト）
         通常のビルド成果物
```

---

## 外部コンテンツの配置（www/ ディレクトリ）

動画・音声・大きな画像など EXE に埋め込みたくないファイルは、EXE と同じフォルダに `www/` ディレクトリを作成して置くだけで自動マウントされます。

```
MyApp.exe
www/
    index.html          → https://app.local/index.html（EXE 埋め込みより優先）
    assets/
        video.mp4       → https://app.local/assets/video.mp4
        bgm.ogg         → https://app.local/assets/bgm.ogg
```

`www/` はメイン ZIP より優先されるため、EXE を再ビルドせずに任意のファイルを差し替えることができます。開発中は EXE 隣に `www/` を置いて編集するだけでリロードに反映されます。

---

## 配布時のコンテンツパッキング

開発時は `www/` フォルダに置くだけで動作しますが、配布時はコンテンツを ZIP にまとめて EXE に埋め込むか、ZIP ファイルとして同梱します。

`pack_zip.py` はテキスト系ファイル（HTML/CSS/JS/JSON）を圧縮し、動画・音声・画像などすでに圧縮済みの形式は無圧縮（ZIP_STORED）で格納します。無圧縮エントリはメモリ展開なしにシーク可能なため、大容量アセットに最適です。

### EXE に埋め込む場合（小〜中規模コンテンツ）

```powershell
# web-content/ を ZIP 化して EXE に埋め込む（ビルド時に自動実行）
msbuild src\WebView2AppHost.csproj /p:Configuration=Release /p:Platform=x64
```

`web-content/` の ZIP 化は MSBuild が自動で行います。

### ZIP を別ファイルとして同梱する場合（大容量コンテンツ）

EXE と同名の `.zip` として配置すると自動的に読み込まれます。EXE を再ビルドせずにコンテンツだけ更新できます。

```powershell
# コンテンツフォルダを ZIP 化
python scripts\pack_zip.py web-content dist\MyApp.zip

# EXE と ZIP を同じフォルダに配置して頒布
dist    MyApp.exe
    MyApp.zip        ← 自動的に読み込まれる
    MyApp.exe.config
    *.dll
```

### EXE に ZIP を結合して単一ファイルにする場合

---

## 配布 EXE の作成（EXE 結合方式）

EXE 末尾に ZIP を連結した単一ファイルを生成できます。

```powershell
python scripts\bundle.py src\bin\x64\Release\net472\WebView2AppHost.exe resources\app.zip dist\MyApp.exe
```

生成された `dist\MyApp.exe` は DLL 類を同梱すれば単体で動作します。

### 仕組み

`bundle.py` は EXE の末尾に ZIP と 12 バイトのトレーラーを付与します。

```
[EXE 本体] [ZIP 本体] [ZIP サイズ: 8 バイト] ["WZGM": 4 バイト]
```

---

## 頒布に必要なファイル

ビルド成果物は `src\bin\x64\Release\net472\` に生成されます。

**頒布時に必要なファイル（最小構成）**

```
WebView2AppHost.exe
WebView2AppHost.exe.config
Microsoft.Web.WebView2.Core.dll
Microsoft.Web.WebView2.WinForms.dll
WebView2Loader.dll
```

`runtimes\win-x64\native\WebView2Loader.dll` はルートの `WebView2Loader.dll` と同内容なので、頒布時はどちらか一方で構いません。EXE 結合方式（`bundle.py`）で配布 EXE を作る場合も、DLL 類は同梱が必要です。

---

## ウィンドウ設定（app.conf.json）

`web-content/app.conf.json` にウィンドウの初期設定を記述します。ファイルがない場合はすべてデフォルト値が使われます。

```json
{
  "title": "My App",
  "width": 1280,
  "height": 720,
  "fullscreen": false
}
```

| キー | 型 | デフォルト | 説明 |
|------|----|-----------|------|
| `title` | 文字列 | `"WebView2 App Host"` | 起動直後のウィンドウタイトル。WebView2 初期化後は `<title>` タグの内容に自動追従する |
| `width` | 整数 | `1280` | ウィンドウの初期幅（クライアント領域・ピクセル） |
| `height` | 整数 | `720` | ウィンドウの初期高さ（クライアント領域・ピクセル） |
| `fullscreen` | 真偽値 | `false` | `true` にすると起動時からフルスクリーンになる |

ウィンドウは画面中央に配置されます。

---

## localStorage の永続化

WebView2 は `localStorage` や `IndexedDB` などのブラウザストレージをユーザーデータディレクトリに保存します。このテンプレートでは EXE のファイル名からディレクトリを自動決定します。

```
%LOCALAPPDATA%\<EXEのファイル名(拡張子なし)>\WebView2\
```

たとえば `MyApp.exe` であれば `%LOCALAPPDATA%\MyApp\WebView2\` になります。アプリ側の JS は通常のブラウザと同じように `localStorage` を使うだけで永続化されます。

---

## JavaScript API（`window.GameBridge`）

Web コンテンツの JS から Win32 ネイティブの機能を呼び出すための API です。WebView2 の初期化完了後、すべてのページで自動的に利用可能になります。

### メソッド一覧

```js
// アプリを終了する（appClosing イベントを経由して閉じる）
GameBridge.exitApp();

// フルスクリーン表示をトグルする（F11 キーと同等）
GameBridge.toggleFullscreen();

// 現在フルスクリーン状態かどうかを取得する（Promise<boolean>）
const isFs = await GameBridge.isFullscreen();

// 閉じることを確定する（appClosing リスナー内から呼ぶ）
GameBridge.confirmClose();

// 閉じるをキャンセルする（appClosing リスナー内から呼ぶ）
GameBridge.cancelClose();
```

### ライフサイクルイベント

#### `appClosing`（beforeunload 相当）

× ボタン・Alt+F4・`GameBridge.exitApp()` でウィンドウを閉じようとしたとき発火します。

```js
window.addEventListener('appClosing', () => {
    // セーブ処理など

    // ⚠️ 必ず confirmClose() か cancelClose() のどちらかを呼ぶこと。
    // どちらも呼ばないとウィンドウが永久に閉じられなくなります。
    GameBridge.confirmClose();
});
```

閉じる前に確認ダイアログを出す例：

```js
window.addEventListener('appClosing', () => {
    if (confirm('終了しますか？')) {
        GameBridge.confirmClose();
    } else {
        GameBridge.cancelClose();  // キャンセル後も再度閉じようとしたとき正常に動く
    }
});
```

#### `visibilitychange`

ウィンドウの最小化・他アプリへの切替で発火します。標準の `document.visibilityState` も同時に更新されます。

```js
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        // BGM を停止、ゲームをポーズ など
    } else {
        // BGM を再開、ゲームを再開 など
    }
});
```

### ブラウザでの開発について

`window.GameBridge` はホストアプリが注入するオブジェクトですが、ブラウザで直接 `index.html` を開いた場合は自動的に stub が有効になり、Web 標準の Fullscreen API や `window.close()` にフォールバックします。追加のセットアップなしにブラウザ上で開発・動作確認が行えます。

### ネイティブ機能の追加

JS から呼び出せる機能を追加するには、`App.cs` の `OnWebMessageReceived()` 内の `switch` 文に分岐を追加します。

```csharp
// C# 側（App.cs）
case "saveData":
    var dataMatch = Regex.Match(msg, @"""data""\s*:\s*""([^""]+)""");
    if (dataMatch.Success)
        File.WriteAllText("save.dat", dataMatch.Groups[1].Value);
    break;
```

```js
// JS 側
window.chrome.webview.postMessage(
    JSON.stringify({ cmd: 'saveData', data: 'hello' })
);
```

---

## キーボードショートカット

| キー | 動作 |
|------|------|
| F11 | フルスクリーン切替 |
| ESC | フルスクリーン解除（フルスクリーン中のみ） |

---

## よくある質問

**Q. WebView2 ランタイムのインストールが必要ですか？**

Windows 11 および最新の Windows 10 には WebView2 ランタイムが標準で含まれています。それより古い環境では [Evergreen ランタイム](https://developer.microsoft.com/microsoft-edge/webview2/) のインストールが必要です。

**Q. アプリのアイコンを変えるには？**

`resources/app.ico` を差し替えてリビルドしてください。このファイルは EXE ファイル自体のアイコン（エクスプローラーで表示されるもの）に使われます。ウィンドウのタイトルバーやタスクバーのアイコンは `<link rel="icon">` の favicon に自動追従します。

```html
<!-- 外部ファイルなしで絵文字をアイコンにする例 -->
<link rel="icon" type="image/svg+xml"
      href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><text y='.9em' font-size='90'>🎮</text></svg>">
```

**Q. 開発中に DevTools を開くには？**

`App.cs` の `InitWebViewAsync()` 内、settings の設定部分を以下のように変更してください。

```csharp
wv.Settings.AreDevToolsEnabled            = true;   // リリース時は false に戻すこと
wv.Settings.AreDefaultContextMenusEnabled = true;  // 右クリックメニューも有効に
```

設定後は F12 キーで DevTools を開けます。

**Q. WebAssembly (WASM) は動きますか？**

動作します。`.wasm` ファイルには `application/wasm` の MIME タイプを返すため、ブラウザと同じ条件で実行できます。

---

## ライセンス

MIT — `LICENSE` ファイルを参照してください。  
[Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) は BSD-3-Clause License で配布されています。
