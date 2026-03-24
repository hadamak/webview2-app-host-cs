# Steam Cloud

Steam Cloud syncs files through Steam.

Typical uses:

- Save files
- Settings
- JSON-based progress data

## Save text data

```js
await Steam.writeCloudFileText(
    'save.json',
    JSON.stringify({ level: 3, hp: 42 })
);
```

## Read text data

```js
const result = await Steam.readCloudFileText('save.json');
console.log(result.text);
```

## List files

```js
const result = await Steam.listCloudFiles();
console.log(result.files);
```
