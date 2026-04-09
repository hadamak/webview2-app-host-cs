# Generic DLL Plugin

Use `DllConnector` when you need the lowest-latency path from JavaScript into .NET code.

## Typical fit

- Win32 wrappers
- local compute and parsing
- existing .NET business logic
- direct access to libraries such as Steamworks

## Structured config

```json
{
  "connectors": [
    { "type": "dll", "path": "plugins/SystemMonitor.dll" }
  ]
}
```

Legacy `loadDlls` is still supported.

## JS usage

```js
const value = await Host.SystemMonitor.Status.GetSnapshot();
```

The bridge resolves `Host.<Alias>.<Class>.<Method>()` through `ReflectionDispatcherBase`, including async methods, instance handles, and selected events.
