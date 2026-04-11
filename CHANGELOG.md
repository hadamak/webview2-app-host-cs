# Changelog

## [Unreleased / Major Evolution]

### 🚀 Core Architecture

- WebView2 App Host のコアを全面的に再設計
  - `App`, `Program`, `AppConfig` の責務を整理し、構造化設定に対応
  - `NavigationPolicy` を独立コンポーネントとして導入
  - `WebResourceHandler` / `SubStream` によるリソース制御を整理
- コンテンツ配信基盤を強化
  - `ZipContentProvider` を大幅改善（キャッシュ・優先順位・ストリーム処理）
  - ZIP / ディレクトリ / 埋め込みリソースの統合的な取り扱い

---

### 🔌 Plugin / Connector Architecture（大規模追加）

- 汎用プラグインアーキテクチャを新規導入
  - `IConnector` ベースの抽象化
  - `ConnectorFactory` による動的生成
- 複数のコネクタ実装を追加
  - `SidecarConnector`（外部プロセス連携）
  - `DllConnector`（ネイティブ/マネージドDLL）
  - `BrowserConnector`
  - `PipeClientConnector` / `PipeServerConnector`
  - `McpConnector`
- リフレクションベースのディスパッチ基盤
  - `ReflectionDispatcherBase` による動的メソッド呼び出し
- メッセージング基盤
  - `MessageBus`, `WebMessageHelper`

---

### 🔄 Sidecar / 外部ランタイム統合

- Sidecarモデルを本格実装
  - StdIOベースのプロセス通信
  - JSON-RPC 2.0 ベースのプロトコル
- マルチランタイム対応
  - Node.js / Python / PowerShell サンプル追加
- プロセス管理強化
  - 自動再起動
  - readyシグナル同期
  - タイムアウト制御
- Node.jsエージェントは最終的に削除され、より汎用的な構成へ移行

---

### 🌐 Browser / CDP Integration

- CDP（Chrome DevTools Protocol）プロキシ実装
  - HTTPメソッド・ボディ転送対応
- Browser操作APIの整理
  - `IBrowserTools` 導入・拡張
  - 不要な内部APIを削除

---

### 🔐 Security / Execution Modes

- Secure Offline モード導入
  - 外部接続制御
  - コネクタ有効化制御
- ナビゲーション制御の強化
  - 外部遷移ポリシー
  - ホストフィルタリング

---

### 🧪 Testing Infrastructure

- 大規模テスト基盤追加
  - `HostTests`（統合テスト）
  - `SidecarTests`, `McpTests`, `SecureOfflineTests`
- テスト用DLL / Sidecar / Node agent などの検証環境
- テスト用Webコンテンツ（proxy, permissions, steamなど）

---

### 🎮 Steam Integration（段階的に実装 → 抽象化）

- Steamworks統合を実装
  - JS → C# → Steam API ブリッジ
- 実装内容
  - achievements / leaderboard / cloud など
- 後に汎用プラグイン構造へ統合
  - Steam特化実装から汎用Connectorへ進化

---

### 📦 Build / Distribution / CI

- GitHub Actions を整備
  - build / release ワークフロー
- 配布構造の整理
  - ZIPパッケージング
  - EXE + ZIP バンドル方式
- 不要なビルドスクリプト削除
  - `bundle.py`, `pack_zip.py` 廃止

---

### 📄 Documentation（大規模整備）

- docs 配下を全面的に再構成
  - architecture / api / guides / maintainer
- プラグイン・Sidecar・Steamの詳細ドキュメント
- READMEの大幅リライト
  - 利用者 / メンテナ向けに整理
- 日本語ドキュメント追加

---

### ⚙️ Configuration System

- `app.conf.json` ベースの構成を拡張
  - コネクタ定義
  - ナビゲーションポリシー
  - Sidecar設定
- 構造化コンフィグへ移行（旧フラット設定から脱却）
- 不要設定の削除（例: sub_streams）

---

### 🧹 Cleanup / Breaking Changes

- 旧API・内部実装の削除
  - AppBridge / WebMessage依存の削除
  - 内部Connectorの整理
- Node.jsエージェント削除
- 古いドキュメント・構成の削除
- .NET Framework 4.8 へ統一

---

### ✨ Misc Improvements

- ログ機構の強化
  - 機微情報の扱い改善
  - 診断メッセージの強化
- スクリーンショット機能改善
- UI / サンプルコンテンツ更新
