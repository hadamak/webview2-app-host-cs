using System.Threading;
using System.Threading.Tasks;

namespace WebView2AppHost
{
    /// <summary>
    /// McpConnector が browser_* ツールを提供するための最小インターフェース。
    /// WebView2 依存を切り離し、テストやヘッドレスでも McpConnector を単体でコンパイルできるようにする。
    /// </summary>
    public interface IBrowserTools
    {
        Task<string> EvaluateAsync(string script, CancellationToken ct = default);
        Task<(string Base64, int Width, int Height)> ScreenshotAsync(CancellationToken ct = default);
        Task NavigateAsync(string url, CancellationToken ct = default);
        Task<string> GetUrlAsync(CancellationToken ct = default);
        Task<string> GetContentAsync(CancellationToken ct = default);
    }
}

