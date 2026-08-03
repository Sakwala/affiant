namespace Affiant.Transport.SignalR.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Transport.SignalR.Extensions;
using Affiant.Transport.SignalR.Tests.Infrastructure;
using Affiant.Transport.SignalR.Tests.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Spins up an in-process TestServer with <see cref="TestAffiantHub"/> and the full
/// SignalR transport stack. Tests create individual HubConnections via
/// <see cref="CreateConnectedClientAsync"/> and dispose them after use.
/// </summary>
public sealed class TransportIntegrationTestFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private TestServer? _server;

    public TestServer Server => _server ?? throw new InvalidOperationException("Fixture not initialized.");

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IChatSessionStore, NullChatSessionStore>();
        builder.Services.AddAffiantSignalR<TestAffiantHub>();

        _app = builder.Build();
        _app.MapHub<TestAffiantHub>("/hubs/affiant");

        await _app.StartAsync();
        _server = _app.GetTestServer();
    }

    /// <summary>
    /// Creates a new HubConnection connected to the test hub. If <paramref name="groupId"/>
    /// is provided the connection joins that SignalR group before returning.
    /// </summary>
    public async Task<(HubConnection connection, string connectionId)> CreateConnectedClientAsync(
        string? groupId = null,
        CancellationToken ct = default)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/affiant",
                opts =>
                {
                    opts.HttpMessageHandlerFactory = _ => _server!.CreateHandler();
                    // TestServer's in-process handler handles HTTP only; force LongPolling
                    // so SignalR doesn't attempt a WebSocket upgrade over a non-existent port.
                    opts.Transports = HttpTransportType.LongPolling;
                })
            .Build();

        await connection.StartAsync(ct);
        // P4 (area-4): AffiantHub is now Hub<IAffiantHubClient>, whose typed Clients proxy
        // structurally cannot carry a test-only "ConnectionRegistered" push (not a real
        // TransportEvent member) — read the connection's own id client-side instead, which is
        // simpler than the round trip it replaces.
        var connectionId = connection.ConnectionId
            ?? throw new InvalidOperationException("HubConnection.ConnectionId was null after StartAsync.");

        if (groupId is not null)
            await connection.InvokeAsync("JoinGroup", groupId, ct);

        return (connection, connectionId);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

[CollectionDefinition("SignalR Transport")]
public sealed class TransportCollection : ICollectionFixture<TransportIntegrationTestFixture> { }
