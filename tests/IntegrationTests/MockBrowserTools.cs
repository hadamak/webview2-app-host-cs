using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebView2AppHost;

namespace HostTests
{
    public sealed class MockBrowserTools : IBrowserTools
    {
        public List<(string Script, CancellationToken Ct)> EvaluateCalls { get; } = new();
        public Func<string, CancellationToken, Task<string>>? EvaluateImpl { get; set; }
        public string? EvaluateReturnValue { get; set; } = "\"mocked\"";

        public List<(string Url, CancellationToken Ct)> NavigateCalls { get; } = new();
        public Func<string, CancellationToken, Task>? NavigateImpl { get; set; }

        public List<(string Selector, CancellationToken Ct)> ClickCalls { get; } = new();
        public Func<string, CancellationToken, Task>? ClickImpl { get; set; }

        public List<(string Selector, string Text, CancellationToken Ct)> TypeCalls { get; } = new();
        public Func<string, string, CancellationToken, Task>? TypeImpl { get; set; }

        public List<(int X, int Y, CancellationToken Ct)> ScrollCalls { get; } = new();
        public Func<int, int, CancellationToken, Task>? ScrollImpl { get; set; }

        public List<CancellationToken> GetUrlCalls { get; } = new();
        public Func<CancellationToken, Task<string>>? GetUrlImpl { get; set; }
        public string? GetUrlReturnValue { get; set; } = "https://example.com";

        public List<CancellationToken> GetContentCalls { get; } = new();
        public Func<CancellationToken, Task<string>>? GetContentImpl { get; set; }
        public string? GetContentReturnValue { get; set; } = "<html></html>";

        public List<CancellationToken> ScreenshotCalls { get; } = new();
        public Func<CancellationToken, Task<(string Base64, int Width, int Height)>>? ScreenshotImpl { get; set; }
        public (string Base64, int Width, int Height)? ScreenshotReturnValue { get; set; } = ("iVBORw0KGgo=", 800, 600);

        public void Reset()
        {
            EvaluateCalls.Clear();
            NavigateCalls.Clear();
            ClickCalls.Clear();
            TypeCalls.Clear();
            ScrollCalls.Clear();
            GetUrlCalls.Clear();
            GetContentCalls.Clear();
            ScreenshotCalls.Clear();
        }

        public Task<string> EvaluateAsync(string script, CancellationToken ct = default)
        {
            EvaluateCalls.Add((script, ct));
            if (EvaluateImpl != null) return EvaluateImpl(script, ct);
            return Task.FromResult(EvaluateReturnValue ?? "\"\"");
        }

        public Task NavigateAsync(string url, CancellationToken ct = default)
        {
            NavigateCalls.Add((url, ct));
            if (NavigateImpl != null) return NavigateImpl(url, ct);
            return Task.CompletedTask;
        }

        public Task ClickAsync(string selector, CancellationToken ct = default)
        {
            ClickCalls.Add((selector, ct));
            if (ClickImpl != null) return ClickImpl(selector, ct);
            return Task.CompletedTask;
        }

        public Task TypeAsync(string selector, string text, CancellationToken ct = default)
        {
            TypeCalls.Add((selector, text, ct));
            if (TypeImpl != null) return TypeImpl(selector, text, ct);
            return Task.CompletedTask;
        }

        public Task ScrollAsync(int x, int y, CancellationToken ct = default)
        {
            ScrollCalls.Add((x, y, ct));
            if (ScrollImpl != null) return ScrollImpl(x, y, ct);
            return Task.CompletedTask;
        }

        public Task<string> GetUrlAsync(CancellationToken ct = default)
        {
            GetUrlCalls.Add(ct);
            if (GetUrlImpl != null) return GetUrlImpl(ct);
            return Task.FromResult(GetUrlReturnValue ?? "");
        }

        public Task<string> GetContentAsync(CancellationToken ct = default)
        {
            GetContentCalls.Add(ct);
            if (GetContentImpl != null) return GetContentImpl(ct);
            return Task.FromResult(GetContentReturnValue ?? "");
        }

        public Task<(string Base64, int Width, int Height)> ScreenshotAsync(CancellationToken ct = default)
        {
            ScreenshotCalls.Add(ct);
            if (ScreenshotImpl != null) return ScreenshotImpl(ct);
            var ret = ScreenshotReturnValue ?? ("", 0, 0);
            return Task.FromResult<(string, int, int)>(ret);
        }

        public Task<string> GetElementsAsync(CancellationToken ct = default) =>
            Task.FromResult("[]");

        public Task ClickLabelAsync(int index, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ClearLabelsAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
