# Steam Integration

The Steam sample is the reference design for a higher-complexity native bridge.

## Building blocks

- `DllConnector`
- structured `app.conf.json`
- Facepunch.Steamworks runtime next to the host executable

## Why it matters

It demonstrates that WebView2AppHost is not limited to wrapping static HTML. The host can front a desktop-class native integration surface while keeping the browser layer lightweight.

## Sample

See `samples/steam-complete`.
