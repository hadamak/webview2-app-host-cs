Steam Complete Sample

This sample is intended to run inside WebView2AppHost with the Steam support package installed.

Quick start:

1. Start the Steam client and sign in
2. Place this folder as `www/` next to `WebView2AppHost.exe`
3. Make sure `steam_bridge.dll` and `steam_api64.dll` are also next to the EXE
4. Launch `WebView2AppHost.exe`

This sample uses AppID 480 in development mode and demonstrates:

- Steam.init()
- Steam UI launch helpers
- Achievements
- DLC status checks
- Rich presence
- Screenshots
- Web API auth tickets

Note:
The Steam UI does not render as an in-window overlay on top of WebView2 content.
