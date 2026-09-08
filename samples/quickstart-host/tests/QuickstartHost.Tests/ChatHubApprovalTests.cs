namespace QuickstartHost.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// What the hub writes after an approval: the record the gate itself produced, and nothing the hub
/// folded a second time.
/// </summary>
public sealed class ChatHubApprovalTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task An_accepted_amendment_reaches_the_row_through_the_gates_own_fold()
    {
        using var host = new QuickstartHostFactory("Development", seamEnabled: true);
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<IDocketStore>();

        var reason = $"hub-approve-{Guid.NewGuid():N}";
        var filed = await ProposeAsync(client, new Dictionary<string, string>
        {
            ["Employee"] = "Amara Silva",
            ["Reason"] = reason,
        });

        await using var connection = await ConnectAsync(host);
        var ack = await connection.InvokeAsync<DecisionAckDto>(
            "ApproveEntry",
            filed.DocketId,
            new Dictionary<string, object?> { ["LeaveType"] = "Sick" });

        Assert.Equal("approved", ack.Outcome);

        var entry = await store.GetDocketEntryAsync(filed.DocketId, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.NotNull(entry.AmendedAffidavit);

        var written = Assert.Single(await LeaveRequestsAsync(client, reason));
        Assert.Equal("Sick", written.LeaveType);
        Assert.Equal(
            entry.AmendedAffidavit.Fields.Single(f => f.Name == "LeaveType").Value?.ToString(),
            written.LeaveType);
    }

    /// <summary>
    /// The regression #99 names. When an amendment map reaches the gate carrying a field the
    /// Affidavit does not propose, the gate logs it and records <b>no</b> amended Affidavit — the
    /// decision still stands. A hub that folded the raw map itself would write the map's other
    /// values anyway, so the row would hold a value no record on the Docket swears to. The hub hands
    /// the executor <c>AmendedAffidavit ?? Envelope</c> instead, so the row is the proposal.
    /// </summary>
    [Fact]
    public async Task A_map_the_gate_could_not_fold_writes_the_proposal_and_none_of_the_map()
    {
        using var host = new QuickstartHostFactory("Development", seamEnabled: true);
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<IDocketStore>();

        var reason = $"hub-unfoldable-{Guid.NewGuid():N}";
        var filed = await ProposeAsync(client, new Dictionary<string, string>
        {
            ["Employee"] = "Devon Park",
            ["Reason"] = reason,
        });

        await using var connection = await ConnectAsync(host);
        var ack = await connection.InvokeAsync<DecisionAckDto>(
            "ApproveEntry",
            filed.DocketId,
            new Dictionary<string, object?>
            {
                ["Days"] = "9",
                // No such field on the Affidavit, so AffidavitAmendments.Apply refuses the whole
                // map and the gate keeps no amended record.
                ["ApprovedBy"] = "someone",
            });

        Assert.Equal("approved", ack.Outcome);

        var entry = await store.GetDocketEntryAsync(filed.DocketId, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Null(entry.AmendedAffidavit);

        var written = Assert.Single(await LeaveRequestsAsync(client, reason));
        Assert.Equal(5, written.Days);
        Assert.Equal("5", entry.Envelope.Fields.Single(f => f.Name == "Days").Value?.ToString());
    }

    private static async Task<ProposeResponse> ProposeAsync(
        HttpClient client, Dictionary<string, string> overrides)
    {
        var response = await client.PostAsJsonAsync(
            "/api/dev/propose",
            new { sessionId = "hub-approval", overrides });
        response.EnsureSuccessStatusCode();

        var filed = await response.Content.ReadFromJsonAsync<ProposeResponse>(
            Json, CancellationToken.None);
        Assert.NotNull(filed);
        return filed;
    }

    private static async Task<HubConnection> ConnectAsync(QuickstartHostFactory host)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/hubs/affiant",
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                    // The in-process test handler speaks HTTP only, so there is no socket for a
                    // WebSocket upgrade to land on.
                    options.Transports = HttpTransportType.LongPolling;
                })
            .Build();

        await connection.StartAsync(CancellationToken.None);
        return connection;
    }

    private static async Task<IReadOnlyList<LeaveRequestDto>> LeaveRequestsAsync(
        HttpClient client, string search)
    {
        var rows = await client.GetFromJsonAsync<List<LeaveRequestDto>>(
            $"/api/leave-requests?search={Uri.EscapeDataString(search)}",
            Json);
        return rows ?? [];
    }

    private sealed record ProposeResponse(string SessionId, Guid DocketId);

    private sealed record DecisionAckDto(string EntryId, string Outcome, bool AmendmentsPreserved);

    private sealed record LeaveRequestDto(
        int Id, string Employee, string LeaveType, string EndDate, int Days, string Reason);
}
