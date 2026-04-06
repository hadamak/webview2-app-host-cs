using System;

namespace WebView2AppHost
{
    /// <summary>
    /// 旧プラグイン互換のための最小コンテキスト（src-generic 専用）。
    /// </summary>
    public sealed class PluginContext
    {
        public PluginContext(Action<string> postMessage)
        {
            PostMessage = postMessage ?? throw new ArgumentNullException(nameof(postMessage));
        }

        public Action<string> PostMessage { get; }
    }
}

