# CDP Proxy Protocol

## Purpose

`CdpProxyHandler` opens a lower-level browser control path through Chrome DevTools Protocol.

## What it enables

- request and response interception for selected origins
- debugging and profiling workflows not covered by the high-level WebView2 API
- protocol-level experimentation while keeping the host app architecture stable

## Position in the stack

The CDP proxy is optional. It sits beside normal navigation handling and is enabled only when relevant configuration such as proxy origins is present.

## Tradeoff

CDP access is powerful but sharp. The intended use is targeted diagnostics and advanced browser manipulation, not replacing the higher-level connector model for ordinary app logic.
