# Affiant.Transport.SignalR

SignalR transport adapter for the [Affiant framework](https://github.com/Sakwala/affiant) — "sworn provenance for every AI write."

Implements `IStreamingTransport` over ASP.NET Core SignalR and provides `AffiantHub`, an abstract, subclassable `Hub<IAffiantHubClient>` base a host derives from to get typed, compile-time-checked client method calls (`Clients.Caller.ReceiveToken(chunk)` instead of a string-keyed `SendAsync`) for every `TransportEvent` the framework can push: Evidence Card requests, docket expiry notices, UI guidance, and system notifications.

## Quick start

```csharp
// Program.cs
builder.Services.AddAffiantCore();
builder.Services.AddAffiantSignalR<MyHub>();
// ...
app.MapAffiantSignalR<MyHub>(); // or app.MapHub<MyHub>("/hubs/affiant") directly
```

```csharp
public class MyHub(IChatSessionStore chatSessionStore, IStreamingTransport transport)
    : AffiantHub(chatSessionStore, transport)
{
    // host-specific hub methods
}
```

`AddAffiantSignalR<THub>()` registers `SignalRStreamingTransport<THub>` as the `IStreamingTransport` singleton and calls `AddSignalR()` with the hub JSON protocol declared explicitly (camelCase properties, enums as strings) — a host must still map the hub in the pipeline.

## Package contents

| Namespace | Purpose |
|---|---|
| `Affiant.Transport.SignalR.Hubs` | `AffiantHub` — the abstract `Hub<IAffiantHubClient>` base a host subclasses; `IAffiantHubClient` — the typed client-call surface, one method per `TransportEvent` member |
| `Affiant.Transport.SignalR.Transport` | `SignalRStreamingTransport<THub>` — the `IStreamingTransport` implementation, including in-process reviewer-confirmation routing |
| `Affiant.Transport.SignalR.Extensions` | `ServiceCollectionExtensions` — `AddAffiantSignalR<THub>`, `MapAffiantSignalR<THub>` |
| (root) | `SignalROptions` — message-size and detailed-errors configuration |

## Further reading

- [Affiant Framework Specification](https://github.com/Sakwala/affiant/blob/main/docs/affiant-framework-specification.md) — the full design contract, including the streaming transport contract and Rule 6 (UI guidance)
- [Tool Authoring Guide](https://github.com/Sakwala/affiant/blob/main/docs/tool-authoring-guide.md) — write your first Affiant plugin pair

---

*Part of the [Affiant Framework](https://github.com/Sakwala/affiant) | Apache-2.0 License*
