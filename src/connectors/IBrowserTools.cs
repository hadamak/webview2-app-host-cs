using System.Threading;
using System.Threading.Tasks;

namespace WebView2AppHost
{
    /// <summary>
    /// McpConnector が browser_* ツールを提供するための最小インターフェース。
    /// </summary>
    public interface IBrowserTools
    {
        Task<string> EvaluateAsync(string script, CancellationToken ct = default);
        Task<(string Base64, int Width, int Height)> ScreenshotAsync(CancellationToken ct = default);
        Task NavigateAsync(string url, CancellationToken ct = default);
        Task<string> GetUrlAsync(CancellationToken ct = default);
        Task<string> GetContentAsync(CancellationToken ct = default);
        Task ClickAsync(string selector, CancellationToken ct = default);
        Task TypeAsync(string selector, string text, CancellationToken ct = default);
        Task ScrollAsync(int x, int y, CancellationToken ct = default);
    }
}
