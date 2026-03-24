# User Stats

User Stats are numeric values stored in Steam. They are useful for totals, records, and achievement progress.

Typical uses:

- Total play count
- Total score
- Total kills
- Best clear time

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

- Undefined stat name
- Using the int API for a float stat
- Using the float API for an int stat
