using System;

namespace WebView2AppHost
{
    /// <summary>
    /// 旧プラグイン互換用インターフェイス（src-generic 専用）。
    /// 本体は IConnector + MessageBus に移行済み。
    /// </summary>
    public interface IHostPlugin : IDisposable
    {
        string PluginName { get; }
        void Initialize(string configJson);
        void HandleWebMessage(string webMessageJson);
    }
}

