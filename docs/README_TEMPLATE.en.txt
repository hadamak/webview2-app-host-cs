Application User Guide

A guide for getting the most out of this application.

1. System Requirements

The following are required to run this application:

- OS: Windows 10 or later
- Runtime:
  - .NET Framework 4.8–4.8.1 (built into Windows 11; may already be present on Windows 10)
  - Microsoft Edge WebView2 Runtime (built into Windows 11; may already be present on Windows 10)

Note:
If you see the error "Failed to initialize WebView2" on launch, download and install the
"Evergreen Bootstrapper" from the WebView2 download page:
https://developer.microsoft.com/microsoft-edge/webview2/

2. Customizing Settings

You can change the window display settings by creating a user.conf.json file in the same
folder as the EXE. Values in this file take priority over the app's built-in defaults.
Any keys you omit will use the app's default values.

[Key : Description]
- width / height: Window size at startup (in pixels)
- fullscreen: Whether to start in fullscreen mode (true / false)

Example:
{
  "width": 1280,
  "height": 720,
  "fullscreen": false
}

3. Troubleshooting

- Settings not taking effect: After editing the settings file, close and restart the app.

--------------------------------------------------------------------------------

License

For the license of this application and notices for third-party libraries it uses,
please refer to the LICENSE and THIRD_PARTY_NOTICES.md files included in this package.
