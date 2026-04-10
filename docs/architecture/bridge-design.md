# Bridge Design

## Core idea

WebView2AppHost is built as a connector bus, not as a hard-coded wrapper around WebView2.

- `MessageBus` is the routing center.
- `ReflectionDispatcherBase` is the invocation engine.
- connectors remain isolated from one another and only depend on the bus contract.

## Message flow

1. A browser, MCP client, or external process emits JSON-RPC.
2. The originating connector publishes into `MessageBus`.
3. The bus broadcasts to every other connector.
4. Each connector decides whether the message is relevant.
5. Responses flow back through the same bus, so UI, tools, and sidecars stay synchronized.

## Connector families

| Family | Classes | Role |
|---|---|---|
| Browser | `BrowserConnector`, `IBrowserTools` | WebView2 control, capture, evaluation |
| Native | `DllConnector` | In-process .NET DLL invocation |
| Sidecar | `SidecarConnector` | External runtimes over stdio |
| Pipe | `PipeServerConnector`, `PipeClientConnector` | Windows process-to-process integration |
| MCP | `McpConnector`, `McpBridge` | AI tool and context exposure |

## Why this shape

- Low coupling: adding a new connector does not require rewriting the others.
- Predictable transport: JSON-RPC across the board.
- Reuse of dispatch semantics: instance handles, async tasks, and event notifications work the same way across connectors.
- Browser and AI parity: the same backend capability can be consumed by the embedded app or an MCP client.
