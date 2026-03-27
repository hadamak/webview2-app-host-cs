Steam Complete Sample

This sample is intended to run inside WebView2AppHost with the Steam support package installed.

Quick start:

1. Start the Steam client and sign in
2. Place this folder as `www/` next to `WebView2AppHost.exe`
3. Make sure `WebView2AppHost.Steam.dll`, `Facepunch.Steamworks.Win64.dll`, and `steam_api64.dll` are also next to the EXE
4. Launch `WebView2AppHost.exe`

This sample uses AppID 480 in development mode and demonstrates:

- Steam.init()
- Steam UI launch helpers
- Achievements and User Stats
- Steam Cloud file operations
- Ownership and DLC helpers
- Leaderboards
- Rich presence
- Screenshots
- Web API auth tickets

For AppID 480, use the Spacewar-defined names such as `ACH_WIN_ONE_GAME`, `NumGames`, and `Feet Traveled`.
If you switch to your own AppID, replace those values with your own Steamworks API names.

Note:
The Steam UI does not render as an in-window overlay on top of WebView2 content.
Steam Deck is not a supported target for this host because the application is built around Windows WebView2.
