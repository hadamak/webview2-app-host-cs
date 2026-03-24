# Leaderboards

Leaderboards are Steam rankings for scores and times.

Typical uses:

- High scores
- Time attacks
- Stage-specific rankings

## Basic flow

1. Find or create the leaderboard
2. Receive its handle
3. Upload a score
4. Download entries

## Example

```js
const board = await Steam.findOrCreateLeaderboard(
    'Feet Traveled',
    'descending',
    'numeric'
);

await Steam.uploadLeaderboardScore(board.leaderboardHandle, 5000);

const scores = await Steam.downloadLeaderboardEntries(
    board.leaderboardHandle,
    'global',
    0,
    9
);
```
