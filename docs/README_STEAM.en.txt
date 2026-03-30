About the Steam Support Package
====================================

This ZIP contains the Steamworks-related files that are meant to be added 
on top of the standard package.

■ Contents
- WebView2AppHost.Steam.dll: Steam bridge DLL for WebView2AppHost
- Facepunch.Steamworks.Win64.dll: Managed bridge connecting WebView2AppHost to Steamworks SDK
- steam_api64.dll: Steamworks SDK runtime DLL
- Newtonsoft.Json.dll (※Included in the ExtensionBase package)
- steam.js: Proprietary Steam API used from HTML content
- steam-sample/: Complete working sample
- STEAM.md: Start-here overview guide
- steam-docs/: Setup guide, feature guides, and API reference
- LICENSE: License for this application
- THIRD_PARTY_NOTICES.md: Third-party library notices

■ Installation
1.  Extract the standard package (Core).
2.  Extract this Steam support package into the same folder.
3.  Important: Extract the ExtensionBase package into the same folder 
    and ensure `Newtonsoft.Json.dll` is present in the EXE folder.
4.  Add `steam.js` and Steam-related `app.conf.json` settings to your own content.

■ Usage
Use `steam-sample/` as a ready-to-run validation sample if needed.

■ Details
This ZIP is for app developers using the bridge. Start with `STEAM.md`, 
then use `steam-docs/en/` for English documentation.
