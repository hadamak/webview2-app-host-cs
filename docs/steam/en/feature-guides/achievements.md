# Achievements

Achievements are Steam milestones that unlock when the player reaches a condition.

Typical uses:

- Clear the game once
- Beat a secret boss
- Finish a stage under a condition

## Steamworks backend setup required

> **This must be done before writing any code.**
>
> 1. Steamworks Partner site → App Admin → **Achievements**
> 2. Define each achievement (API name, display name, icon)
> 3. Click **Publish**
>
> Calling `unlockAchievement` with a name that has no published definition will always fail.  
> The API does not create achievements automatically.

## Minimal example

```js
const steam = await Steam.init();
if (!steam.isAvailable) return;

await Steam.unlockAchievement('ACH_WIN_ONE_GAME');
```

## Read state

```js
const result = await Steam.getAchievementState('ACH_WIN_ONE_GAME');
console.log(result.isUnlocked);
```
