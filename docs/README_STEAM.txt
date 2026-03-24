Steamサポートパッケージについて

この ZIP は通常版に追加して使う Steamworks 対応ファイル一式です。

含まれている内容:

- steam_bridge.dll: WebView2AppHost と Steamworks SDK を接続する native ブリッジ
- steam_api64.dll: Steamworks SDK のランタイム DLL
- steam.js: HTML 側から使う Steam 独自 API
- steam-sample/: 完全に動作するサンプル
- STEAM.md: アプリ開発者向け Steam 利用ガイド
- LICENSE: 本アプリケーションのライセンス
- THIRD_PARTY_NOTICES.md: サードパーティ製ライブラリの通知

使い方:

1. 通常版の配布物を展開します
2. この Steam サポートパッケージを同じフォルダに展開します
3. 自作コンテンツに steam.js と app.conf.json の Steam 設定を追加します
4. 必要に応じて steam-sample/ をそのまま動作確認に使えます

補足:
Steamworks SDK のダウンロードが必要なのは steam_bridge.dll をビルドし直す人だけです。
通常のアプリ開発者や利用者は、このパッケージ内の steam_bridge.dll / steam_api64.dll を使えば足ります。

詳細:
この ZIP はアプリ開発者向けです。ブリッジのビルド方法はリポジトリ内の `docs/steam/bridge-build.md` を参照してください。
