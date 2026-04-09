# Navigation Policy Redesign

## Problem statement

Current behavior is simple but too rigid:

- `https://app.local/` stays inside the host
- every other `http(s)` URL opens in the system browser
- non-`http(s)` schemes are not modeled explicitly
- `app.conf.json.navigation_policy` exists but is not actually used for navigation routing

This creates a mismatch between configuration and runtime behavior.

## Goals

- keep runtime logic small and auditable
- support configuration-driven behavior without turning navigation into a rule engine
- make `SecureRelease` meaningfully stricter than normal `Release`
- separate what should be fixed at build time from what application authors can vary at deploy time

## Non-goals

- per-URL arbitrary scripting
- complex precedence chains between many rule types
- policy definitions that require users to understand the internals of WebView2 events

## Proposed model

Navigation policy should be expressed in two layers:

### 1. Build-time capability profile

This is owned by the host build and should not be overridable by app content.

Suggested internal profile:

- `Standard`
  - normal release behavior
- `Restricted`
  - secure release behavior

Suggested defaults:

- `Release` => `Standard`
- `SecureRelease` => `Restricted`

### 2. App-level routing policy

This is owned by `app.conf.json` and can only narrow behavior within what the build allows.

Suggested top-level field:

```json
{
  "navigation_policy": {
    "external_navigation_mode": "system_browser",
    "allow_external_hosts": ["*.github.com"],
    "block_external_hosts": ["*.ads.example.com"],
    "allowed_external_schemes": ["https", "mailto"]
  }
}
```

## External navigation modes

Keep the mode set intentionally small:

### `system_browser`

- external URLs are opened by the OS default browser or handler
- host-local URLs remain inside the host
- this should be the default for `Release`

### `whitelist`

- only hosts in `allow_external_hosts` may open externally
- everything else is blocked
- useful for locked-down apps that still need a few known domains

### `block`

- no external URL is opened by the host
- useful when content must stay self-contained
- this should be the default for `SecureRelease`

## Classification result

Replace the current binary result with a small explicit result set:

- `AllowInHost`
- `OpenExternal`
- `Block`

This still keeps the implementation simple in `NavigationStarting` and `NewWindowRequested`.

## Suggested evaluation order

1. If the URL is host-local, return `AllowInHost`
2. Parse scheme
3. If the scheme is not permitted by build profile, return `Block`
4. If the scheme is not in app-level `allowed_external_schemes`, return `Block`
5. Apply `external_navigation_mode`
6. If mode is `system_browser`
   - if host is in `block_external_hosts`, return `Block`
   - else return `OpenExternal`
7. If mode is `whitelist`
   - if host is in `allow_external_hosts`, return `OpenExternal`
   - else return `Block`
8. If mode is `block`
   - return `Block`

This is intentionally linear and avoids rule stacking.

## Build-time responsibilities

Build-time policy should define hard ceilings, not app-specific allow lists.

Recommended responsibilities:

- whether external navigation is available at all
- which scheme families are supported at all
- whether the default mode is `system_browser` or `block`

Recommended mapping:

### `Release`

- allows external navigation
- allows a conservative scheme set such as:
  - `http`
  - `https`
  - optionally `mailto`
- default app mode if omitted: `system_browser`

### `SecureRelease`

- blocks external navigation by default
- either:
  - allows no external schemes, or
  - allows a very small explicit set but still defaults app mode to `block`
- app config may narrow further, but may not widen beyond secure build limits

## Why build-time and app-time are both needed

- app content should not be able to weaken a security-sensitive host build
- normal releases still need flexible per-app policy
- this prevents `SecureRelease` from becoming security theater

## Configuration shape

Recommended replacement for the current partial model:

```json
{
  "navigation_policy": {
    "external_navigation_mode": "whitelist",
    "allow_external_hosts": ["*.github.com", "*.microsoft.com"],
    "block_external_hosts": ["ads.example.com"],
    "allowed_external_schemes": ["https", "mailto"]
  }
}
```

Notes:

- `allow_external_hosts` only matters for `whitelist`
- `block_external_hosts` only matters for `system_browser`
- `allowed_external_schemes` is always enforced
- wildcard host matching is sufficient; no regex is needed

## Migration plan

### Phase 1

- add the new config fields
- keep current behavior as compatibility fallback when fields are omitted

Compatibility fallback:

- missing `external_navigation_mode` in `Release` => `system_browser`
- missing `external_navigation_mode` in `SecureRelease` => `block`

### Phase 2

- route all navigation decisions through config-aware classification
- cover `NavigationStarting` and `NewWindowRequested`
- explicitly handle non-`http(s)` schemes

### Phase 3

- add tests for:
  - host-local routing
  - whitelist allow
  - whitelist deny
  - block mode
  - secure build ceiling
  - `mailto` and other explicit schemes

## Recommendation

Implement the redesign around:

- one build-time profile
- one app-level mode
- two host lists
- one allowed-scheme list
- three final actions

That is enough to cover the practical cases:

- open all external URLs externally
- open only whitelisted destinations
- open nothing externally

without turning `NavigationPolicy` into a large policy engine.
