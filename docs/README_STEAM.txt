Steamサポートパッケージについて
====================================

この ZIP は通常版に追加して使う Steamworks 対応ファイル一式です。

■ 含まれている内容
- WebView2AppHost.Steam.dll: WebView2AppHost 用の Steam ブリッジ DLL
- Facepunch.Steamworks.Win64.dll: C# 製の Steamworks 連携ライブラリ
- steam_api64.dll: Steamworks SDK のランタイム DLL
- Newtonsoft.Json.dll (※ExtensionBase パッケージに含まれます)
- steam.js: HTML 側から使う Steam 独自 API
- steam-sample/: 完全に動作するサンプル
- STEAM.md: 最初に読む概要ガイド
- steam-docs/: 導入手順、機能別ガイド
- LICENSE: 本アプリケーションのライセンス
- THIRD_PARTY_NOTICES.md: サードパーティ製ライブラリの通知

■ 導入方法
1.  通常版の配布物 (Core) を展開します。
2.  この Steam サポートパッケージを同じフォルダに展開します。
3.  重要: ExtensionBase パッケージを展開し、`Newtonsoft.Json.dll` も同じフォルダに配置してください。
4.  自作コンテンツに `steam.js` と `app.conf.json` の Steam 設定を追加します。

■ 使い方
必要に応じて `steam-sample/` をそのまま動作確認に使えます。

■ 詳細
この ZIP はアプリ開発者向けです。まず `STEAM.md` を読み、詳細は `steam-docs/` を参照してください。
