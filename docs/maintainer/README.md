# Maintainer Docs

This section is for people changing the host itself rather than simply shipping content on top of it.

## Read this section if you need to

- modify connector behavior
- change routing or dispatch rules
- maintain MCP support
- adjust packaging behavior in the host codebase
- reason about compatibility and long-term design tradeoffs

## Core internals

- [../architecture/bridge-design.md](../architecture/bridge-design.md)
- [../architecture/mcp-integration.md](../architecture/mcp-integration.md)
- [../architecture/cdp-proxy-protocol.md](../architecture/cdp-proxy-protocol.md)

## Compatibility and roadmap

- [api-compatibility.md](api-compatibility.md)
- [revision-proposal.md](revision-proposal.md)
- [future-considerations.md](future-considerations.md)

## Operational references

- [../guides/build-and-release.md](../guides/build-and-release.md)
- [../guides/content-packaging.md](../guides/content-packaging.md)
- [../api/connector-interfaces.md](../api/connector-interfaces.md)
- [../api/app-conf-json.md](../api/app-conf-json.md)

## Separation of concerns

- Root `README.md` and `README.ja.md`
  - adopter-oriented, content packaging first
- `docs/maintainer/`
  - host implementation and maintenance concerns
