# Steam 対応の概要

この文書は、Steamworks を詳しく知らないブラウザゲーム開発者向けの概要です。

このホストの Steam 対応は、次の流れを前提にしています。

1. 通常版の WebView2AppHost を使う
2. Steam 対応が必要なら Steam サポート ZIP を追加する
3. HTML / JavaScript 側から `steam.js` を呼ぶ

つまり、通常の利用者は `steam_bridge.dll` をビルドする必要がありません。  
Steamworks SDK のダウンロードが必要なのは、ブリッジ自体を改修する担当者だけです。

---

## 何ができるか

### 実績

ゲーム内の到達条件を満たしたときに、Steam の実績を解除します。

例:

- 1 回クリアした
- 特定条件でステージを突破した
- 隠し要素を見つけた

### User Stats

Steam に保存される数値データです。実績の判定材料や累計記録に向いています。

例:

- プレイ回数
- 累計スコア
- 総撃破数
- 最速クリア時間

### Steam Cloud

セーブデータや設定データを Steam 経由で同期します。

例:

- セーブスロット
- 設定ファイル
- 進行状況 JSON

### Leaderboards

Steam のランキング機能です。

例:

- ハイスコア
- タイムアタック
- デイリーランキング

### Rich Presence

Steam のフレンド一覧に、いま何をしているかを表示する機能です。

例:

- タイトル画面にいる
- ステージ 3 をプレイ中
- 協力プレイのロビー待機中

### 所有権確認 / DLC

本編や DLC の所有状態、インストール状態を確認します。

例:

- DLC ステージを開放してよいか
- サウンドトラック DLC を持っているか
- Family Sharing 経由かどうか

---

## ⚠️ コードを書く前に必要な Steamworks 管理画面の設定

**「ZIP を追加すれば即 Steam 対応」ではありません。**  
各機能は、Steamworks Partner サイト（App Admin）での事前設定が完了していないと動作しません。

| 機能 | 必要な事前設定 | 未設定時の症状 |
|------|---------------|---------------|
| **実績** | App Admin → Achievements で実績を定義し、Publish する | `SetAchievement` が常に失敗する / 実績が表示されない |
| **User Stats** | App Admin → Stats で stat 名・型・初期値を定義し、Publish する | `setStat*` / `getStatInt` が `stat-not-found-or-type-mismatch` を返す |
| **Leaderboards** | App Admin → Leaderboards でボードを作成・Publish する | `findOrCreateLeaderboard` が失敗する |
| **Steam Cloud** | App Admin → Steam Cloud を有効化し、クォータ・パスを設定する | `writeCloudFileText` などが常に失敗する |

> **Publish 忘れに注意**: 定義を保存しただけでは不十分です。各設定は「Publish」を実行して初めて有効になります。

詳細は [Step by Step: Stats](https://partner.steamgames.com/doc/tutorial/statistic) および [ISteamUserStats Interface](https://partner.steamgames.com/doc/api/ISteamUserStats) を参照してください。

---

## この実装で意識していること

- Steam 対応の追加は「別 ZIP を展開するだけ」に寄せる
- 使う人は Steamworks SDK を意識しなくてよい
- API 名だけでなく、ゲームでの用途から説明する
- サンプルをそのまま動かして確認できるようにする

---

## 制約

- WebView2 の画面上に Steam オーバーレイが重なるわけではありません
- `showOverlay*` は Steam 側 UI を開くための API です
- Steam Deck はサポート対象外です。このホストは Windows 版 WebView2 前提で、SteamOS / Proton 上の動作は保証しません

---

## 次に読む文書

- 最短導入手順: `docs/steam/getting-started.md`
- 機能ごとの説明: `docs/steam/feature-guides/`
- 関数一覧: `docs/steam/api-reference.md`
