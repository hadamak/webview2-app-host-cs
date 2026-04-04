# Steam Integration Overview

WebView2 App Host provides an environment to call Steamworks API directly from JavaScript using the **GenericDllPlugin**.

---

## How it works: Generic DLL Passthrough

The backend uses **[Facepunch.Steamworks](https://wiki.facepunch.com/steamworks/)**.
`GenericDllPlugin` receives messages from JavaScript and uses reflection to call the C# API of Facepunch.Steamworks.

```
JavaScript (ES6 Proxy)
  └─ Host.Steam.ClassName.MethodName(args)
        ↓ JSON { method: "Steam.ClassName.Method", params: { args: [...] } }
GenericDllPlugin (C#)
  └─ Steamworks.ClassName.MethodName(args) ← Facepunch.Steamworks
        ↓ Result
JavaScript (Promise)
```

### Features
- **No API additions needed**: You can directly access all classes, methods, and properties provided by Facepunch.Steamworks from JavaScript.
- **Standard calls**: Call methods using the same names as in the official C# documentation.
- **Event reception**: Receive callback events (such as achievement unlock notifications) from Steam in JavaScript.

---

## How to call from JavaScript

**Just specify the class and method names as they appear in the official Facepunch.Steamworks C# documentation.**

```js
// 1. Initialization
await Host.Steam.SteamClient.Init(480, true);

// 2. Unlocking an achievement
await Host.Steam.SteamUserStats.SetAchievement('ACH_WIN_ONE_GAME');
await Host.Steam.SteamUserStats.StoreStats();

// 3. Getting a stat
const wins = await Host.Steam.SteamUserStats.GetStatInt('NumWins');

// 4. Opening the Steam Overlay
await Host.Steam.SteamFriends.OpenOverlay('achievements');

// 5. Saving a screenshot
// Capture the WebView2 screen to a file, then register it with Steam.
const preview = await Host.Internal.WebView.CapturePreview("screenshot.png");
await Host.Steam.SteamScreenshots.AddScreenshot(
    preview.path, "", preview.width, preview.height
);

// 6. Using async methods and instances
const board = await Host.Steam.SteamUserStats.FindOrCreateLeaderboardAsync('Feet Traveled', 2, 1);
await board.SubmitScoreAsync(100);
```

Reference: [Facepunch.Steamworks Documentation](https://wiki.facepunch.com/steamworks/)

---

## Steam Callback Events

Events triggered by Steam can be received using `Host.on()`.

```js
// Achievement progress notification
Host.on('OnAchievementProgress', ({ achievementName, currentProgress, maxProgress }) => {
    console.log(`${achievementName}: ${currentProgress}/${maxProgress}`);
});

// Steam Overlay activation/deactivation
Host.on('OnGameOverlayActivated', ({ active }) => {
    if (active) pauseGame();
    else resumeGame();
});
```

---

## Limitations

- **No large data transfer**: Due to communication limits between the host and JavaScript, you cannot transfer large binary data (like screenshots) directly as numeric arrays.
- **Overlay**: The Steam Overlay does not render on top of the WebView2 window (only OS notifications etc. are visible).
- **Platform**: Steam Deck / SteamOS are not supported (Windows WebView2 only).
