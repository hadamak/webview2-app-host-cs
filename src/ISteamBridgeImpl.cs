using System;

namespace WebView2AppHost
{
    /// <summary>
    /// SteamBridge シェルと SteamBridgeImpl の疎結合インターフェース。
    /// SteamBridge.cs は Facepunch.Steamworks の型を直接参照しないため、
    /// このインターフェース経由で実体クラスを呼び出す。
    /// </summary>
    public interface ISteamBridgeImpl : IDisposable
    {
        void HandleWebMessage(string webMessageJson);
    }
}
