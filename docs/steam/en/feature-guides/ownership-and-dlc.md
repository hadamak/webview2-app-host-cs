# Ownership and DLC

These helpers let you check whether the user owns the app or a DLC and whether DLC content is installed.

Typical uses:

- Unlock a DLC stage
- Enable soundtrack or character packs
- Check family sharing state

## Ownership

```js
const info = await Steam.getAppOwnershipInfo();
console.log(info.isSubscribed);
console.log(info.isSubscribedFromFamilySharing);
```

## Check a specific AppID

```js
const result = await Steam.isSubscribedApp(123456);
console.log(result.isSubscribed);
```

## DLC list

```js
const result = await Steam.getDlcList();
console.log(result.dlc);
```
