# Steam ドキュメント案内

Steam 関連の文書は対象者ごとに分離しています。

- アプリ開発者向け入口: `docs/steam/overview.md`
- 最短導入手順: `docs/steam/getting-started.md`
- 関数一覧: `docs/steam/api-reference.md`

前者 3 つは、ビルド済みの Steam サポート ZIP を受け取って使う人向けです。通常は Steamworks SDK のダウンロードは不要です。

Steam ブリッジ DLL (`WebView2AppHost.Steam.dll`) 自体をビルド・改修する場合は `src-steam/` を参照してください。Steamworks SDK をローカルに用意する必要があります。
