About the Steam Support Package

This ZIP contains the Steamworks-related files that are meant to be added on top of the standard package.

Contents:

- Facepunch.Steamworks.Win64.dll: Managed bridge connecting WebView2AppHost to Steamworks SDK
- steam_api64.dll: Steamworks SDK runtime DLL
- steam.js: Proprietary Steam API used from HTML content
- steam-sample/: Complete working sample
- STEAM.md: Start-here overview guide
- steam-docs/: Setup guide, feature guides, and API reference
- LICENSE: License for this application
- THIRD_PARTY_NOTICES.md: Third-party library notices

Usage:

1. Extract the standard package
2. Extract this Steam support package into the same folder
3. Add `steam.js` and Steam-related `app.conf.json` settings to your own content
4. Use `steam-sample/` as a ready-to-run validation sample if needed

Note:
Downloading the Steamworks SDK is only necessary for people who rebuild `Facepunch.Steamworks`.
Regular app developers and end users can simply use the `Facepunch.Steamworks.Win64.dll` and `steam_api64.dll` included in this package.

Details:
This ZIP is for app developers using the bridge. Start with `STEAM.md`, then use `steam-docs/en/` for English documentation.
