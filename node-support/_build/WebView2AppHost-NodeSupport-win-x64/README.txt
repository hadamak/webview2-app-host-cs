WebView2 App Host - Node.js Support
====================================

このパッケージは、WebView2 App Host に Node.js 連携機能を追加するための拡張モジュールです。
JavaScript からローカルの Node.js サイドカープロセスを呼び出し、ファイル操作や外部プロセス実行などの
サーバーサイド機能をブラウザ側から利用可能にします。

■ 導入方法
1.  WebView2AppHost.exe と同じフォルダに、以下のファイルを配置します。
    - WebView2AppHost.Node.dll
    - Newtonsoft.Json.dll (※ExtensionBase パッケージに含まれます)
    - node-runtime/ フォルダ (server.js, package.json を含む)

2.  重要: Node.js ランタイム (node.exe) の配置
    本パッケージには node.exe は含まれていません。
    公式サイト (https://nodejs.org/) 等から Windows x64 版の node.exe をダウンロードし、
    node-runtime/ フォルダの中に配置してください。

    配置後の構成例:
    WebView2AppHost.exe
    WebView2AppHost.Node.dll
    Newtonsoft.Json.dll
    node-runtime/
    ├── node.exe  <-- 手動で配置
    ├── server.js
    └── package.json

3.  app.conf.json の設定
    app.conf.json の "plugins" 配列に "Node" を追加します。
    例: { "plugins": ["Node"] }

■ 使い方 (JavaScript)
テスト用の `node-test.html` を参照してください。
WebView2 の postMessage を使用して、Node.js 側へメッセージを送信できます。

■ 注意点
- サイドカープロセスは標準入出力 (StdIO) で通信します。
- メッセージは NDJSON (改行区切り) 形式でやり取りされます。
- node.exe のバージョンは v20 以降を推奨します。
