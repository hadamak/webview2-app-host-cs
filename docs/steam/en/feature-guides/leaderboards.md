# Leaderboards

Leaderboards are Steam rankings for scores and times.

Typical uses:

- High scores
- Time attacks
- Stage-specific rankings

## Steamworks backend setup required

> **This must be done before writing any code.**
>
> 1. Steamworks Partner site → App Admin → **Leaderboards**
> 2. Create the leaderboard (name, sort order, display type)
> 3. Click **Publish**
>
> `findOrCreateLeaderboard` will attempt to create a board if one does not exist, but in production  
> only boards pre-defined in the backend are reliable.  
> Scores uploaded to an undefined board will not appear for other players.

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
