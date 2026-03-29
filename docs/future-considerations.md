# 今後の検討事項

実装上のボトルネックや仕様改善の候補を記録するドキュメント。

---

## 1. ZIP 内ファイルへの Range Request 対応 ✅ 実装済み

### 現状
`ReadOnlyStream` が `CanSeek = false` のため、ZIP エントリに対する Range Request（`bytes=N-M`）は非対応。動画・音声・WASM など Range Request が必要なファイルは `www/` への個別配置が必要で、「コンテンツを EXE にまとめて配布したい」という主要な特徴と衝突する。

### 背景
以前、圧縮済みコンテンツを無圧縮で ZIP に格納し Range Request に対応するパッカースクリプトを実装したバージョンがあった。専用パッカーが必要になること・コードの複雑化というデメリットから、`www/` 配置のみに絞るシンプル化を選択した経緯がある。

### 対応
エントリ全体を `MemoryStream` に展開して返すよう `ZipSource.OpenEntry` を変更。`ReadOnlyStream` クラスは不要となり削除。
大きなファイルはメモリを圧迫するため、動画・音声等は引き続き `www/` への個別配置を推奨。

---

## 2. 外部オリジン CORS プロキシ ✅ 実装済み

### 現状
`https://app.local/` から外部 API（別ドメインの `http(s)://`）への `fetch` は CORS でブロックされる。コンテンツ側でサーバーレスプロキシを用意するか、あきらめるしかない。

### 対応
`proxyOrigins` に登録した外部オリジンを `AddWebResourceRequestedFilter` で登録し、`HandleWebResourceRequest` で `HttpClient` 転送。コンテンツ側は通常の URL で `fetch` でき、ブラウザとの互換性が保たれる。ホスト固有の URL 記述不要。

### 残存制限
WebView2 の `WebResourceRequested` イベントでは `e.Request.Content`（リクエストボディ）が常に null を返す既知の制約があり、POST / PUT 等のボディ転送は非対応。GET リクエストのみ機能する。解決するには `WebResourceResponseReceived` と `NavigateWithWebResourceRequest` を組み合わせる手法が必要だが、実装コストが高い。

---

## 3. エンドユーザー向け設定ファイルの分離 ✅ 実装済み

### 現状
`app.conf.json` は要素ごとの上書きができないため、エンドユーザーがウィンドウサイズだけを変更したい場合でも `app.conf.json` を丸ごと記述する必要があり、コンテンツ制作者が設定した他の値を上書きしてしまう。

### 方針
コンテンツ制作者が規定する設定（タイトル・推奨サイズ・フルスクリーン等）は引き続き ZIP / 埋め込みの `app.conf.json` で管理する。エンドユーザーが変更できる設定（ウィンドウサイズ・フルスクリーン）は EXE 隣接の別ファイル（例：`user.conf.json`）として切り出す。

### 対応
`AppConfig.ApplyUserConfig(exeDir)` を追加。EXE 隣接の `user.conf.json` から `width`・`height`・`fullscreen` のみ上書き可能。`title` は変更不可。範囲外の値はクランプ、パース失敗は警告ログのみで無視。

---

## 4. ポップアップウィンドウのコンテンツソース

### 現状
`OpenHostPopup` は毎回 `new ZipContentProvider()` を生成して `Load()` を呼ぶ。`TryAddArgSource()` はプロセス全体の `Environment.GetCommandLineArgs()` を参照するため、コマンドライン引数で渡した ZIP はポップアップでも正しく参照される。

### 残存する制限
コマンドライン引数・`www/` フォルダ・同名 ZIP・連結 ZIP・埋め込みリソースというすべての標準ルートではポップアップが同一コンテンツを取得できる。ただし、ポップアップごとに独立した `ZipContentProvider` インスタンスを持つため、同一 ZIP ファイルへの複数ファイルハンドルが発生する。通常の運用では問題にならないが、多数のポップアップを同時に開く場合はファイルハンドル数の増加に注意が必要。

また、ホストが独立した `App` インスタンスとしてポップアップを作るため、`e.NewWindow` に `CoreWebView2` を渡せず `window.open()` の戻り値が常に `null` になる。ポップアップウィンドウ自体は開くが、JS 側から参照を介した操作（`popup.close()` 等）はできない。

### 対応案
メインウィンドウの `ZipContentProvider` を参照渡しで共有する設計も可能だが、ポップアップのライフサイクル管理（どちらが先に Dispose するか）が複雑になるため、現状の独立インスタンス方式を維持することが妥当。
`window.open()` の戻り値問題は `e.NewWindow` に子ウィンドウの `CoreWebView2` を渡す設計で解決できる。ただし、親から子を閉じる用途は一般的でなく、子が自分で閉じるか X ボタンで閉じる運用で十分なため現状維持が妥当。実装すると非同期の `GetDeferral` フローが加わりコードが複雑になるデメリットもある。

---

## 5. Steam DLL の本格的なコード署名検証 （未実装・将来の検討）

### 現状
`SteamBridge.TryCreate` は `Assembly.LoadFrom` で EXE 隣接の `WebView2AppHost.Steam.dll` をロードする。
現在は `FileVersionInfo` でバージョン番号を照合する簡易チェックのみ実施している。

### 課題
EXE と同じディレクトリに悪意ある同名 DLL を置かれた場合、バージョン番号を偽装されればロードされてしまう。
ただし、この攻撃が成立するには書き込み権限が必要であり、その時点でアプリ全体が危殆化していると見なせるため、優先度は低い。

### 対応案
- **コード署名検証**: `Authenticode` を用いて DLL の署名者を検証する。配布時に EXE と DLL を同一証明書で署名する運用が前提になる。
- **ハッシュ検証**: リリース時に SHA-256 ハッシュを EXE 内のリソースに埋め込み、ロード前に照合する。配布スクリプト（`release.yml`）でハッシュを埋め込む自動化が必要。

いずれも Steam 対応が商用リリース段階に達した時点で再検討する。
