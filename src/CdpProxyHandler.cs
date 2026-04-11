using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace WebView2AppHost
{
    /// <summary>
    /// CDP（Chrome DevTools Protocol）の Fetch ドメインを使った透過 CORS プロキシ。
    ///
    /// 動作フロー:
    ///   1. Fetch.enable で proxyOrigins に一致するリクエストをインターセプト
    ///   2. Fetch.requestPaused イベントでリクエスト詳細（URL・メソッド・ヘッダ・ボディ）を取得
    ///   3. HttpClient で実際の外部 API へ転送
    ///   4. Fetch.fulfillRequest でレスポンスを WebView2 に返す
    ///
    /// 以前の WebResourceRequested ベースの実装（GET 専用）と比べ、
    /// POST / PUT / DELETE など任意のメソッドとリクエストボディを透過的に転送できる。
    ///
    /// 制限事項:
    ///   - postData は文字列型。multipart/form-data 等の純バイナリボディは
    ///     文字化けする可能性がある（JSON / application/x-www-form-urlencoded は問題なし）。
    ///   - CDP がボディを省略した場合（hasPostData=true かつ postData=null）は
    ///     ボディなしで転送される（警告ログを出力）。
    ///   - 非常に大きなレスポンスはメモリを圧迫する可能性がある。
    ///
    /// スレッドモデル:
    ///   DevToolsProtocolEventReceived は UI スレッドで発火する。
    ///   async void ハンドラ内の await は WinForms の SynchronizationContext で
    ///   UI スレッドに復帰するため、CallDevToolsProtocolMethodAsync は
    ///   常に UI スレッドから呼ばれる（WebView2 の要件を満たす）。
    /// </summary>
    internal sealed class CdpProxyHandler : IDisposable
    {
        private readonly CoreWebView2 _coreWebView;
        private readonly string[]     _proxyOrigins;
        private readonly HttpClient   _httpClient;

        // DataContractJsonSerializer を一度だけ生成してキャッシュする。
        // UseSimpleDictionaryFormat = true により Dictionary<string, string> が
        // JSON オブジェクト形式で正しくデシリアライズされる（.NET 4.5 以降）。
        private readonly DataContractJsonSerializer _eventSerializer;

        private CoreWebView2DevToolsProtocolEventReceiver? _receiver;
        private bool _disposed;

        // ---------------------------------------------------------------------------
        // コンストラクタ
        // ---------------------------------------------------------------------------

        public CdpProxyHandler(CoreWebView2 coreWebView, string[] proxyOrigins, HttpClient httpClient)
        {
            _coreWebView   = coreWebView;
            _proxyOrigins  = proxyOrigins;
            _httpClient    = httpClient;
            _eventSerializer = new DataContractJsonSerializer(
                typeof(CdpFetchRequestPausedParams),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        }

        // ---------------------------------------------------------------------------
        // 有効化
        // ---------------------------------------------------------------------------

        /// <summary>
        /// CDP Fetch ドメインを有効化し、proxyOrigins へのリクエストの
        /// インターセプトを開始する。
        /// proxyOrigins が空の場合は何もしない。
        /// </summary>
        public async Task EnableAsync()
        {
            if (_proxyOrigins.Length == 0) return;

            // Fetch.enable パラメータ: 各 proxyOrigin へのリクエストを Request ステージでパウズ
            var patternsJson = string.Join(",",
                _proxyOrigins.Select(origin =>
                {
                    var pattern = EscapeJsonString(origin.TrimEnd('/') + "/*");
                    return $"{{\"urlPattern\":\"{pattern}\",\"requestStage\":\"Request\"}}";
                }));
            var enableParams = $"{{\"patterns\":[{patternsJson}],\"handleAuthRequests\":false}}";

            await _coreWebView.CallDevToolsProtocolMethodAsync("Fetch.enable", enableParams);

            _receiver = _coreWebView.GetDevToolsProtocolEventReceiver("Fetch.requestPaused");
            _receiver.DevToolsProtocolEventReceived += OnRequestPaused;

            AppLog.Log(AppLog.LogLevel.Info, "CdpProxyHandler.EnableAsync",
                $"CDP 透過プロキシ有効化: {_proxyOrigins.Length} オリジン");
        }

        // ---------------------------------------------------------------------------
        // CDP イベントハンドラ（UI スレッドで発火）
        // ---------------------------------------------------------------------------

        private async void OnRequestPaused(
            object sender,
            CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
        {
            // パウズされたリクエストは必ず fulfillRequest か failRequest で終了させる。
            // そうしないとブラウザがリクエストをハングさせたままになる。
            string? requestId = null;
            try
            {
                var @params = ParseEvent(e.ParameterObjectAsJson);
                if (@params == null)
                {
                    AppLog.Log(AppLog.LogLevel.Warn, "CdpProxyHandler",
                        "Fetch.requestPaused イベントのパースに失敗しました");
                    return;
                }

                requestId = @params.RequestId;
                await HandlePausedRequestAsync(@params);
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, "CdpProxyHandler.OnRequestPaused", ex.Message, ex);
                if (requestId != null)
                    await TryFailRequestAsync(requestId);
            }
        }

        // ---------------------------------------------------------------------------
        // イベント JSON のパース
        // ---------------------------------------------------------------------------

        private CdpFetchRequestPausedParams? ParseEvent(string json)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                using var ms = new MemoryStream(bytes);
                return (CdpFetchRequestPausedParams?)_eventSerializer.ReadObject(ms);
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, "CdpProxyHandler.ParseEvent", ex.Message, ex);
                return null;
            }
        }

        // ---------------------------------------------------------------------------
        // リクエスト転送メイン処理
        // ---------------------------------------------------------------------------

        private async Task HandlePausedRequestAsync(CdpFetchRequestPausedParams @params)
        {
            if (_disposed) return;

            var req = @params.Request;
            if (req == null)
            {
                await TryFailRequestAsync(@params.RequestId);
                return;
            }

            // HTTP リクエストを構築
            var method      = new HttpMethod(req.Method ?? "GET");
            var httpRequest = new HttpRequestMessage(method, req.Url);

            // リクエストヘッダを転送（ホップバイホップヘッダは除外）
            if (req.Headers != null)
            {
                foreach (var kv in req.Headers)
                {
                    if (IsHopByHopHeader(kv.Key)) continue;
                    try { httpRequest.Headers.TryAddWithoutValidation(kv.Key, kv.Value); }
                    catch { /* 無効なヘッダは無視 */ }
                }
            }

            // リクエストボディを転送
            if (req.HasPostData)
            {
                if (req.PostData != null)
                {
                    // postData は UTF-8 テキストとして扱う
                    // （バイナリコンテンツは未サポート。REST API の JSON / form-urlencoded は問題なし）
                    var bodyBytes = Encoding.UTF8.GetBytes(req.PostData);
                    httpRequest.Content = new ByteArrayContent(bodyBytes);

                    // 元のリクエストの Content-Type を引き継ぐ
                    var contentType = FindHeader(req.Headers, "Content-Type");
                    if (!string.IsNullOrEmpty(contentType))
                    {
                        try
                        {
                            httpRequest.Content.Headers.ContentType =
                                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
                        }
                        catch { /* 無効な Content-Type は無視 */ }
                    }
                }
                else
                {
                    // CDP がボディを省略した場合（大きなボディで発生し得る）。
                    // Fetch.getRequestPostData で取得する実装は将来の課題とし、
                    // 現時点はボディなしで転送して警告を出す。
                    AppLog.Log(AppLog.LogLevel.Warn, "CdpProxyHandler",
                        $"hasPostData=true だが postData が null です。ボディなしで転送します: {req.Url}");
                }
            }

            // 外部 API へ転送
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    httpRequest, HttpCompletionOption.ResponseContentRead);
            }
            catch (TaskCanceledException)
            {
                AppLog.Log(AppLog.LogLevel.Warn, "CdpProxyHandler.HandlePausedRequest",
                    $"タイムアウト: {req.Method} {req.Url}");
                await TryFailRequestAsync(@params.RequestId);
                return;
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Error, "CdpProxyHandler.HandlePausedRequest",
                    $"転送失敗: {req.Method} {req.Url}", ex);
                await TryFailRequestAsync(@params.RequestId);
                return;
            }

            if (_disposed) return; // 転送中に Dispose された場合

            // レスポンスボディを base64 エンコード（Fetch.fulfillRequest の要件）
            var body       = await response.Content.ReadAsByteArrayAsync();
            var bodyBase64 = Convert.ToBase64String(body);

            // レスポンスヘッダを構築（CORS ヘッダを付与）
            var responseHeaders = BuildResponseHeaders(response);

            var fulfillParams =
                $"{{" +
                $"\"requestId\":\"{EscapeJsonString(@params.RequestId)}\"," +
                $"\"responseCode\":{(int)response.StatusCode}," +
                $"\"responseHeaders\":[{string.Join(",", responseHeaders)}]," +
                $"\"body\":\"{bodyBase64}\"" +
                $"}}";

            await _coreWebView.CallDevToolsProtocolMethodAsync("Fetch.fulfillRequest", fulfillParams);

#if DEBUG
            AppLog.Log(AppLog.LogLevel.Info, "CdpProxyHandler",
                $"{req.Method} {req.Url} → {(int)response.StatusCode} ({body.Length} bytes)");
#endif
        }

        // ---------------------------------------------------------------------------
        // レスポンスヘッダの構築
        // ---------------------------------------------------------------------------

        private static IEnumerable<string> BuildResponseHeaders(HttpResponseMessage response)
        {
            var headers = new List<string>();

            // 1. 元のヘッダをコピー（ただし、転送してはいけないものは除外する）
            AddFilteredHeaders(headers, response.Headers);
            AddFilteredHeaders(headers, response.Content.Headers);

            // 2. CORS ヘッダおよび制御用ヘッダを付与
            // Access-Control-Allow-Origin 等が重複するとブラウザ側で Failed to fetch になるため、
            // 元のレスポンスに含まれていた場合は AddFilteredHeaders で除外されている。
            headers.Add("{\"name\":\"Access-Control-Allow-Origin\",\"value\":\"*\"}");
            headers.Add("{\"name\":\"Access-Control-Allow-Methods\",\"value\":\"GET, POST, PUT, DELETE, OPTIONS\"}");
            headers.Add("{\"name\":\"Access-Control-Allow-Headers\",\"value\":\"*\"}");
            headers.Add("{\"name\":\"Access-Control-Expose-Headers\",\"value\":\"*\"}");
            headers.Add("{\"name\":\"Cache-Control\",\"value\":\"no-store\"}");

            return headers;
        }

        /// <summary>
        /// HTTP レスポンスヘッダからパッシブなヘッダのみを抽出し、プロキシ転送用のリストに追加する。
        /// CORS ヘッダやホップバイホップヘッダ（Transfer-Encoding等）は、
        /// ブラウザ側の動作や重複によるエラーを防ぐために除外する。
        /// </summary>
        private static void AddFilteredHeaders(List<string> dest, System.Net.Http.Headers.HttpHeaders headers)
        {
            foreach (var h in headers)
            {
                var name = h.Key;

                // ホップバイホップヘッダを除外
                if (IsHopByHopHeader(name)) continue;

                // CORS ヘッダはホスト側で一括制御するため除外（重複防止）
                if (name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)) continue;

                // Content-Length は FulfillRequest 時に自動計算されるか、
                // 送信データと矛盾する可能性があるため除外
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var v in h.Value)
                {
                    dest.Add($"{{\"name\":\"{EscapeJsonString(name)}\",\"value\":\"{EscapeJsonString(v)}\"}}");
                }
            }
        }

        // ---------------------------------------------------------------------------
        // フォールバック: Fetch.failRequest
        // ---------------------------------------------------------------------------

        /// <summary>
        /// 転送失敗時にブラウザのリクエストハングを防ぐため、
        /// Fetch.failRequest を呼んでリクエストを終了させる。
        /// 例外は握りつぶす（失敗の失敗は対処できないため）。
        /// </summary>
        private async Task TryFailRequestAsync(string requestId)
        {
            if (_disposed) return;
            try
            {
                await _coreWebView.CallDevToolsProtocolMethodAsync(
                    "Fetch.failRequest",
                    $"{{\"requestId\":\"{EscapeJsonString(requestId)}\",\"errorReason\":\"Failed\"}}");
            }
            catch (Exception ex)
            {
                AppLog.Log(AppLog.LogLevel.Warn, "CdpProxyHandler.TryFailRequestAsync", ex.Message, ex);
            }
        }

        // ---------------------------------------------------------------------------
        // ヘルパー
        // ---------------------------------------------------------------------------

        /// <summary>
        /// ヘッダ辞書から大文字小文字を無視してヘッダ値を検索する。
        /// </summary>
        private static string? FindHeader(Dictionary<string, string>? headers, string name)
        {
            if (headers == null) return null;
            if (headers.TryGetValue(name, out var v)) return v;
            // ケース違いのフォールバック（CDP のヘッダ名は小文字で届くこともある）
            foreach (var kv in headers)
                if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        /// <summary>
        /// ホップバイホップヘッダ（プロキシで書き換えるべき、または中継してはいけない管理用ヘッダ）を判定する。
        /// </summary>
        private static bool IsHopByHopHeader(string name) =>
            name.Equals("Host",              StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Origin",            StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Referer",           StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Connection",        StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Keep-Alive",        StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Proxy-Authenticate",StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Proxy-Authorization",StringComparison.OrdinalIgnoreCase) ||
            name.Equals("TE",                StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Trailers",          StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Upgrade",           StringComparison.OrdinalIgnoreCase);

        private static string EscapeJsonString(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"")
             .Replace("\n",  "\\n" ).Replace("\r",  "\\r").Replace("\t", "\\t");

        // ---------------------------------------------------------------------------
        // IDisposable
        // ---------------------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_receiver != null)
                _receiver.DevToolsProtocolEventReceived -= OnRequestPaused;

            // Fetch ドメインを無効化（ベストエフォート、CoreWebView2 が既に破棄済みの場合は無視）
            try
            {
                _ = _coreWebView.CallDevToolsProtocolMethodAsync("Fetch.disable", "{}");
            }
            catch { }
        }

        // ---------------------------------------------------------------------------
        // CDP イベント データコントラクト
        // ---------------------------------------------------------------------------

        [DataContract]
        private sealed class CdpFetchRequestPausedParams
        {
            [DataMember(Name = "requestId")]
            public string RequestId { get; set; } = "";

            [DataMember(Name = "request")]
            public CdpRequest? Request { get; set; }
        }

        [DataContract]
        private sealed class CdpRequest
        {
            [DataMember(Name = "url")]
            public string Url { get; set; } = "";

            [DataMember(Name = "method")]
            public string Method { get; set; } = "GET";

            /// <summary>
            /// CDP ではヘッダが JSON オブジェクト形式で届く。
            /// DataContractJsonSerializerSettings.UseSimpleDictionaryFormat = true により
            /// Dictionary&lt;string, string&gt; として正しくデシリアライズされる。
            /// </summary>
            [DataMember(Name = "headers")]
            public Dictionary<string, string>? Headers { get; set; }

            /// <summary>
            /// HTTP リクエストボディ（テキスト形式）。
            /// hasPostData=true でも省略される場合がある（大きなボディ等）。
            /// </summary>
            [DataMember(Name = "postData")]
            public string? PostData { get; set; }

            [DataMember(Name = "hasPostData")]
            public bool HasPostData { get; set; }
        }
    }
}