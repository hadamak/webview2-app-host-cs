# Web API 対応状況

このドキュメントは、標準ブラウザ API のうちデスクトップアプリとして意味を持つものを網羅的に列挙し、
WebView2 における対応状況と本ホストの実装状況を整理したものです。

優先度の凡例:

| 記号 | 意味 |
|------|------|
| ★★★ | 多くのアプリで必要になる可能性が高い |
| ★★☆ | 用途によっては重要 |
| ★☆☆ | 特殊用途・代替手段あり |
| —   | デスクトップアプリでは不要または非該当 |

実装状況の凡例:

| 記号 | 意味 |
|------|------|
| ✅ | ホストが対応済み（または WebView2 が透過的に処理） |
| 🔲 | 未対応（対応可能） |
| ❌ | WebView2 が未サポートまたは対応困難 |
| — | 非該当 |

---

## ウィンドウ管理

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `window.close()` | ★★★ | `WindowCloseRequested` | ✅ | `beforeunload` 経由でキャンセル可能 |
| `beforeunload` | ★★★ | `Navigate("about:blank")` 経由 | ✅ | X ボタン・`window.close()` 両方に対応 |
| `requestFullscreen()` / `exitFullscreen()` | ★★★ | `ContainsFullScreenElementChanged` | ✅ | |
| `visibilityState` / `visibilitychange` | ★★☆ | `PostWebMessageAsString` | ✅ | 最小化・復元に連動 |
| `window.resizeTo()` / `resizeBy()` | ★☆☆ | なし（Form 操作で実装可能） | 🔲 | |
| `window.moveTo()` / `moveBy()` | ★☆☆ | なし（Form 操作で実装可能） | 🔲 | |
| `window.screenX` / `screenY` | ★☆☆ | なし（Form.Location で実装可能） | 🔲 | JS からの読み取りは `ExecuteScriptAsync` で応答 |
| `window.outerWidth` / `outerHeight` | ★☆☆ | なし（Form.Size で実装可能） | 🔲 | 同上 |
| `window.focus()` | ★☆☆ | なし（`Form.Activate()` で実装可能） | 🔲 | |
| `window.print()` | ★☆☆ | `CoreWebView2.ShowPrintUI()` | 🔲 | WebView2 に印刷 UI あり |

---

## ナビゲーション・リンク

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `window.open()` ポップアップ (app.local) | ★★★ | `NewWindowRequested` | ✅ | ホスト内ウィンドウとして開く |
| `window.open()` 外部 URL | ★★★ | `NewWindowRequested` | ✅ | 既定ブラウザで開く |
| `<a target="_blank">` 外部 URL | ★★★ | `NewWindowRequested` | ✅ | 同上 |
| `<a href="...">` 外部 URL (`_self`) | ★★★ | `NavigationStarting` | ✅ | キャンセルして既定ブラウザへ |
| カスタムプロトコルハンドラ (`registerProtocolHandler`) | ★☆☆ | `AddWebResourceRequestedFilter` で部分的に代替可能 | 🔲 | `https://app.local/` 以外のスキームは非対応 |

---

## ダイアログ

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `window.alert()` | ★★★ | WebView2 が標準ダイアログを表示 | ✅ | ホスト側の実装不要 |
| `window.confirm()` | ★★★ | WebView2 が標準ダイアログを表示 | ✅ | 同上 |
| `window.prompt()` | ★★★ | WebView2 が標準ダイアログを表示 | ✅ | 同上 |
| `<input type="file">` ファイル選択ダイアログ | ★★★ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |
| File System Access API (`showOpenFilePicker` 等) | ★★☆ | WebView2 が透過的に処理 | ✅ | WebView2 側でダイアログを提供 |
| `beforeunload` キャンセルダイアログ | ★★☆ | `Navigate("about:blank")` 経由 | ✅ | |
| `ScriptDialogOpening` カスタマイズ | ★☆☆ | `ScriptDialogOpening` | 🔲 | ダイアログの見た目をホスト側で差し替え可能 |

---

## 権限・メディアデバイス

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `navigator.mediaDevices.getUserMedia()` カメラ・マイク | ★★☆ | `PermissionRequested` | 🔲 | 未ハンドル時は WebView2 が既定の許可/拒否ダイアログを表示 |
| `navigator.mediaDevices.getDisplayMedia()` 画面キャプチャ | ★☆☆ | `PermissionRequested` | 🔲 | 同上 |
| `Notification` API | ★☆☆ | `PermissionRequested` | 🔲 | 未ハンドル時は拒否。`NotificationReceived` で制御可能 |
| Geolocation API | ★☆☆ | `PermissionRequested` | 🔲 | |
| Permissions API (`navigator.permissions.query`) | ★☆☆ | `PermissionRequested` と連動 | 🔲 | ハンドルしなければ WebView2 の既定に従う |
| `navigator.getBattery()` | — | WebView2 非サポート | ❌ | デスクトップアプリでは通常不要 |

---

## ダウンロード

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `<a download>` / `URL.createObjectURL` ダウンロード | ★★☆ | `DownloadStarting` | 🔲 | 未ハンドル時は WebView2 の既定保存ダイアログが表示される |
| `DownloadStarting` でダウンロードパス制御 | ★☆☆ | `DownloadStarting` | 🔲 | 保存先の制御・キャンセルが可能 |

---

## クリップボード・共有

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| Clipboard API (`navigator.clipboard`) | ★★★ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |
| Drag & Drop | ★★☆ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |
| Web Share API (`navigator.share()`) | ★☆☆ | WebView2 非サポート | ❌ | Windows の共有 UI は別途 P/Invoke で実装可能 |

---

## 認証・セキュリティ

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| Web Authentication API (WebAuthn / Passkey) | ★★☆ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |
| Basic 認証ダイアログ | ★☆☆ | `BasicAuthenticationRequested` | 🔲 | 未ハンドル時は WebView2 が標準ダイアログを表示 |

---

## コンテキストメニュー

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| 右クリックメニューの無効化 | ★★★ | `AreDefaultContextMenusEnabled = false` | ✅ | 現在は完全無効 |
| カスタムコンテキストメニュー | ★☆☆ | `ContextMenuRequested` | 🔲 | アプリ独自のメニューを提供可能 |

---

## ネットワーク・通信

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `fetch` / `XMLHttpRequest` (same-origin) | ★★★ | WebView2 が透過的に処理 | ✅ | `app.local` 内のリクエストは `WebResourceRequested` で配信 |
| WebSocket | ★★★ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |
| Service Worker | ★☆☆ | WebView2 が部分的にサポート | ✅ | `https://app.local/` のサービスワーカーは動作する |
| `fetch` CORS (外部オリジン) | ★★☆ | `WebResourceRequested` でプロキシ実装可能 | 🔲 | 外部 API へのアクセスが必要な場合はコンテンツ側で CORS を解決するか、ホスト側でプロキシを挟む |

---

## メディア再生

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `<video>` / `<audio>` 再生 | ★★★ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |
| Range Request (動画シーク等) | ★★☆ | `WebResourceRequested` | ✅ | 実装済み |
| WebRTC | ★☆☆ | WebView2 が透過的に処理 | ✅ | `PermissionRequested` でカメラ・マイクの許可が必要 |
| Web Audio API | ★★☆ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |

---

## アプリバッジ・OS 統合

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `navigator.setAppBadge()` | ★☆☆ | WebView2 非サポート | ❌ | Windows タスクバーバッジは `ITaskbarList3` (P/Invoke) で実装可能 |
| タスクバーの進捗表示 | ★☆☆ | WebView2 非サポート | ❌ | `ITaskbarList3` で実装可能 |
| システムトレイ | — | WebView2 非サポート | ❌ | `NotifyIcon` (WinForms) で実装可能 |
| `IdleDetector` | — | WebView2 非サポート | ❌ | |

---

## スクリーン・表示

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| `window.matchMedia('(prefers-color-scheme)')` | ★★☆ | WebView2 が透過的に処理 | ✅ | OS のカラースキームに自動追従 |
| Window Management API (`getScreenDetails()`) | ★☆☆ | `PermissionRequested` (`WindowManagement`) | 🔲 | マルチモニター対応アプリで必要 |
| `screen.orientation` | — | WebView2 が透過的に処理 | ✅ | デスクトップではほぼ固定 |

---

## キーボード・入力

| API | 優先度 | WebView2 イベント/機能 | 実装状況 | 備考 |
|-----|--------|----------------------|----------|------|
| キーボードイベント全般 | ★★★ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |
| `AcceleratorKeyPressed` (F5 リロード等の抑制) | ★★☆ | `AcceleratorKeyPressed` | 🔲 | 未ハンドル時は WebView2 のデフォルト動作（F5 でリロードなど）が有効 |
| IME / 日本語入力 | ★★★ | WebView2 が透過的に処理 | ✅ | ホスト側の実装不要 |

---

## 優先度まとめ

実装コストと効果のバランスから、未対応（🔲）の中で対応を検討する順序の目安:

### 高優先度
1. **`AcceleratorKeyPressed`（F5 リロード等の抑制）** — リリースビルドでエンドユーザーが F5 でリロードしてしまう問題を防ぐ。実装が軽い。
2. **`PermissionRequested`（カメラ・マイク・通知）** — 許可が必要なアプリでは必須。未ハンドルでも動作はするが、デフォルト挙動に依存することになる。

### 中優先度
3. **`DownloadStarting`（ダウンロード制御）** — 未ハンドルでも WebView2 の標準ダイアログが出るため致命的ではないが、保存先を固定したいケースで必要。
4. **`window.resizeTo()` / `moveTo()`** — ゲームや特定用途で使われる。WinForms 側での実装は容易だが、JS→C# のメッセージングが必要。

### 低優先度（特定用途）
5. **`window.print()`** — `CoreWebView2.ShowPrintUI()` が使えるため実装コストは低い。
6. **`ScriptDialogOpening`（ダイアログのカスタマイズ）** — デフォルトでも動作するため、見た目の統一が必要な場合のみ。
7. **`ContextMenuRequested`（カスタムコンテキストメニュー）** — 現在は完全無効化。アプリ要件による。
