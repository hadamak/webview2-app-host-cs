# Navigation Policy Redesign

## Current model

`navigation_policy` now routes external URLs with four explicit modes:

- `host`
- `browser`
- `rules`
- `block`

The routing lists are:

- `open_in_host`
- `open_in_browser`
- `block`
- `allowed_external_schemes`
- `block_request_patterns`

## Intent

- keep classification linear and auditable
- support "open everything in-host" without introducing a rule engine
- keep `SecureRelease` stricter through build-time ceilings

## Classification result

- `Allow`
  - keep navigation inside the host
- `OpenExternal`
  - send to the OS default browser or handler
- `Block`
  - reject in both places

## Evaluation order

1. `https://app.local/...` => `Allow`
2. invalid or `about:blank` => `Allow`
3. secure build => `Block`
4. unsupported scheme => `Block`
5. `allowed_external_schemes` mismatch => `Block`
6. host matches `block` => `Block`
7. apply `external_navigation_mode`

Mode behavior:

- `host`
  - `http(s)` => `Allow`
- `browser`
  - `http(s)` => `OpenExternal`
- `rules`
  - `open_in_host` => `Allow`
  - `open_in_browser` => `OpenExternal`
  - otherwise => `Block`
- `block`
  - `http(s)` => `Block`

Non-`http(s)` schemes such as `mailto` still go to `OpenExternal` when allowed by scheme.

## Example

```json
{
  "navigation_policy": {
    "external_navigation_mode": "rules",
    "open_in_host": ["*.github.com"],
    "open_in_browser": ["login.microsoftonline.com"],
    "block": ["ads.example.com"],
    "allowed_external_schemes": ["https", "mailto"],
    "block_request_patterns": ["*ads*"]
  }
}
```

## Build profile expectations

- `Release`
  - default mode if omitted: `browser`
- `SecureRelease`
  - effective ceiling: `block`

App config may narrow behavior, but secure builds still win.
