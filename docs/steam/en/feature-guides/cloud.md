# Steam Cloud

Steam Cloud syncs files through Steam.

## Steamworks backend setup required

> **This must be done before writing any code.**
>
> 1. Steamworks Partner site → App Admin → **Steam Cloud**
> 2. Enable "Enable Cloud Support for \<App Name\>"
> 3. Set the quota (byte limit and file count limit)
> 4. Click **Publish**
>
> Calling `writeCloudFileText` or any other Cloud API without completing the above will always fail.  
> Reference: [Steam Cloud — Steamworks Documentation](https://partner.steamgames.com/doc/features/cloud)

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
