using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WebView2AppHost
{
    /// <summary>
    /// request id → 応答 JSON をブリッジする小さな同期機構。
    /// stdin/stdout の MCP サーバー層と、プラグイン層（Dll/Sidecar/Browser 等）を疎結合にする。
    /// </summary>
    public sealed class McpBridge
    {
        private static readonly JavaScriptSerializer s_json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);

        public event Action<string>? UnsolicitedMessage;

        /// <summary>
        /// プラグイン側から届いた JSON を受け取り、id が一致する pending を完了させる。
        /// id がない場合は UnsolicitedMessage に流す。
        /// id があり一致しない場合は、MCP起因のリクエストの遅延応答であれば警告を出す。
        /// </summary>
        public void Dispatch(string messageJson, Dictionary<string, object>? dict = null)
        {
            if (string.IsNullOrWhiteSpace(messageJson))
                return;

            string? id = null;
            try
            {
                dict ??= s_json.Deserialize<Dictionary<string, object>>(messageJson);
                if (dict != null && dict.TryGetValue("id", out var idObj) && idObj != null)
                    id = idObj.ToString();
            }
            catch
            {
                // パース不能な文字列も「不意のメッセージ」として流す
            }

            if (string.IsNullOrEmpty(id))
            {
                UnsolicitedMessage?.Invoke(messageJson);
                return;
            }

            if (_pending.TryRemove(id!, out var tcs))
            {
                AppLog.Log(AppLog.LogLevel.Info, "McpBridge", $"id 一致: {id}");
                tcs.TrySetResult(messageJson);
                return;
            }

            // 自分(MCP)が発行したリクエストの遅延応答・タイムアウト後の応答のみ警告を出す。
            // メッセージバスにはリクエストメッセージ自体も流れるため、
            // レスポンス(result または error を持つ)である場合のみチェックする。
            if (id!.StartsWith("mcp-"))
            {
                bool isResponse = dict != null && (dict.ContainsKey("result") || dict.ContainsKey("error"));
                if (isResponse)
                {
                    AppLog.Log(AppLog.LogLevel.Warn, "McpBridge", $"一致する pending id が見つかりません（タイムアウトまたは重複）: {id} (現在の pending: {string.Join(", ", _pending.Keys)})");
                }
            }
        }

        public Task<string> CallAsync(
            string requestJson,
            string id,
            Action<string> sendToPlugin,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (sendToPlugin == null) throw new ArgumentNullException(nameof(sendToPlugin));
            if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromMilliseconds(1);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs))
                throw new InvalidOperationException($"duplicate id: {id}");

            try
            {
                sendToPlugin(requestJson);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }

            return WaitAsync(id, tcs, timeout, ct);
        }

        private async Task<string> WaitAsync(
            string id,
            TaskCompletionSource<string> tcs,
            TimeSpan timeout,
            CancellationToken ct)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                using (linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            catch (TaskCanceledException)
            {
                _pending.TryRemove(id, out _);
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
                throw new TimeoutException($"MCP call timed out: {id}");
            }
        }
    }
}
