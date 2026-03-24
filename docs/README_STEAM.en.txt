About the Steam Support Package

This ZIP contains the Steamworks-related files that are meant to be added on top of the standard package.

Contents:

- steam_bridge.dll: Native bridge connecting WebView2AppHost to Steamworks SDK
- steam_api64.dll: Steamworks SDK runtime DLL
- steam.js: Proprietary Steam API used from HTML content
- steam-sample/: Complete working sample
- STEAM.md: Steam usage guide for app developers
- LICENSE: License for this application
- THIRD_PARTY_NOTICES.md: Third-party library notices

Usage:

1. Extract the standard package
2. Extract this Steam support package into the same folder
3. Add `steam.js` and Steam-related `app.conf.json` settings to your own content
4. Use `steam-sample/` as a ready-to-run validation sample if needed

Note:
Downloading the Steamworks SDK is only necessary for people who rebuild `steam_bridge.dll`.
Regular app developers and end users can simply use the `steam_bridge.dll` and `steam_api64.dll` included in this package.

Details:
This ZIP is for app developers using the bridge. For rebuilding the bridge itself, see `docs/steam/bridge-build.md` in the repository.
