# User Stats

User Stats are numeric values stored in Steam. They are useful for totals, records, and achievement progress.

Typical uses:

- Total play count
- Total score
- Total kills
- Best clear time

## Steamworks backend setup required

> **This must be done before writing any code.**
>
> 1. Steamworks Partner site → App Admin → **Stats**
> 2. Define each stat (API name, type `INT` or `FLOAT`, default value)
> 3. Click **Publish**
>
> Calling `setStat*` / `getStat*` with an undefined or unpublished stat name returns `stat-not-found-or-type-mismatch`.  
> The same applies if the int/float type in code does not match the type defined in the backend.

## Int example

```js
await Steam.setStatInt('NumGames', 10);
await Steam.storeStats();
```

```js
const result = await Steam.getStatInt('NumGames');
console.log(result.value);
```

## Float example

```js
await Steam.setStatFloat('FeetTraveled', 1234.5);
await Steam.storeStats();
```

Common failure:

- Stat not defined in Steamworks, or not yet Published
- Undefined stat name
- Using the int API for a float stat
- Using the float API for an int stat
