# Achievements

Achievements are Steam milestones that unlock when the player reaches a condition.

Typical uses:

- Clear the game once
- Beat a secret boss
- Finish a stage under a condition

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
