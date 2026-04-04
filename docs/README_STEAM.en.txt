WebView2 App Host - Steam Integration (via GenericDllPlugin)
==========================================================

This project supports Steamworks integration using the GenericDllPlugin.
You can call the Facepunch.Steamworks API directly from JavaScript.

■ How to use
1. Place the following files next to WebView2AppHost.exe:
   - WebView2AppHost.GenericDllPlugin.dll
   - Facepunch.Steamworks.Win64.dll
   - steam_api64.dll

2. Configure app.conf.json:
   {
     "plugins": ["GenericDllPlugin"],
     "loadDlls": ["Facepunch.Steamworks.Win64.dll"]
   }

3. Call from JavaScript:
   await Host.invoke({
     dllName: "Facepunch.Steamworks.Win64",
     className: "SteamClient",
     methodName: "Init",
     args: [480]
   });

■ Documentation
For more details, see:
- docs/generic-dll-plugin.md (Plugin specification)
- docs/steam/overview.md (Steam specific guide)
