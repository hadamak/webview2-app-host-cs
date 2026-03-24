# Steam アプリ開発者向け案内

この文書は入口です。Steamworks SDK を使ってブリッジ自体をビルドする人向けではなく、ビルド済みの Steam サポート ZIP を使って HTML / JavaScript のゲームを Steam 対応する人向けです。

この文脈で大事なのは次の 2 点です。

- Steamworks SDK のダウンロードは不要です
- ただし Steam の機能をどうゲームに使うかは知る必要があります

このリポジトリの Steam 対応は、「ブリッジのビルドなしで、ブラウザゲームを ZIP 化して Steam リリースできる」を目標にしています。  
そのため、Steam 独自 API は Steamworks を知らない開発者でも追えるよう、機能説明と用途説明を分けて文書化しています。

---

## 最初に読む文書

- 概要と設計思想: `docs/steam/overview.md`
- 最短導入手順: `docs/steam/getting-started.md`
- 関数一覧: `docs/steam/api-reference.md`

---

## 機能ごとの文書

- 実績: `docs/steam/feature-guides/achievements.md`
- User Stats: `docs/steam/feature-guides/stats.md`
- Steam Cloud: `docs/steam/feature-guides/cloud.md`
- Leaderboards: `docs/steam/feature-guides/leaderboards.md`
- Rich Presence: `docs/steam/feature-guides/presence.md`
- 所有権確認 / DLC: `docs/steam/feature-guides/ownership-and-dlc.md`

---

## ブリッジをビルドする場合

`steam_bridge.dll` 自体をビルド・改修する人だけは別文書を参照してください。

- ブリッジビルド担当向け: `docs/steam/bridge-build.md`
