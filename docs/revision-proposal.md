# 改定案（ドキュメント・コードレビュー結果）

本ドキュメントは、現行実装（`src/`）と README 記述の整合を確認したうえで、次の改定候補を優先度順にまとめたものです。

## 1. すぐ反映した修正（完了）

### 起動失敗メッセージの説明不足を解消
- `Program.cs` のエラーメッセージが `app.zip` 前提になっていたため、実際の探索順（引数 ZIP / 隣接 ZIP / EXE 結合 / 埋め込み）に合わせて更新。
- これにより、README の仕様と実行時メッセージの不一致を解消。

---

## 2. 高優先の改定候補

### 2-1. `app.conf.json` パーサーの堅牢化
現状の `AppConfig` は正規表現ベースの最小パーサーで、以下のリスクがあります。
- JSON のエスケープ文字列（例: `"` や `\n`）を厳密にデコードしない。
- 数値や bool の不正値で例外になる可能性がある。

**改定案**
- `System.Text.Json`（導入可能なら）または `DataContractJsonSerializer` へ置換。
- 失敗時は「デフォルト値へフォールバック + 警告ログ出力」の方針を明文化。

### 2-2. Range Request のバリデーション強化
`ParseRange()` は基本動作を満たしていますが、`bytes=100-50` のような逆転レンジや異常値への明示的な 416 応答がありません。

**改定案**
- 無効レンジ時は `416 Range Not Satisfiable` を返す。
- `Content-Range: bytes */<total>` を付与。
- 単体テスト（境界値）を追加。

### 2-3. JS/C# 間メッセージの JSON パース改善
`OnWebMessageReceived()` が正規表現抽出のため、メッセージ構造の変更に弱いです。

**改定案**
- 受信 JSON を DTO にデシリアライズして処理。
- `cmd` の enum 化、未知コマンドのログ化を実施。

---

## 3. 中優先の改定候補

### 3-1. `appClosing` リスナー検知方法の見直し
現在は `window.addEventListener/removeEventListener` を上書きして `appClosing` の登録数を追跡しています。サードパーティライブラリとの干渉余地があります。

**改定案**
- `GameBridge.onAppClosing(handler)` を提供し、内部管理へ寄せる。
- 既存の `addEventListener('appClosing', ...)` は後方互換として段階的に非推奨化。

### 3-2. README の運用セクション拡張
運用時に実際に詰まりやすいポイントを追記。

**追記候補**
- 無効な `app.conf.json` だった場合の挙動。
- Range の対応範囲（単一レンジのみ対応など）。
- `AreDevToolsEnabled` の切替を Debug ビルド条件で管理する例。

---

## 4. 推奨ロードマップ

### フェーズ1（小規模・安全）
1. `Program.cs` 文言修正（完了）
2. README に「設定ファイル不正時の挙動」を明記
3. Range の無効値に対する 416 応答追加

### フェーズ2（堅牢化）
1. `AppConfig` の JSON パーサー置換
2. WebMessage の DTO 化
3. 主要フローの最小テスト追加

### フェーズ3（API 整理）
1. `appClosing` リスナー管理 API の導入
2. 既存 API との互換運用期間を設定

---

## 5. 影響範囲まとめ
- **今回反映済み**: `src/Program.cs`（エラーメッセージのみ）
- **提案のみ**: `src/AppConfig.cs`, `src/App.cs`, `README.md`

