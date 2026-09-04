namespace QuickstartHost.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Docket.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// The development seam: that it files through the real review gate rather than fabricating a
/// docket row, that the entry it files is state the framework's own sweep moves to Expired, that an
/// update swears only what the caller stated, and that it is unreachable anywhere but local
/// development.
/// </summary>
public sealed class DevSeamTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static QuickstartHostFactory Host(string environment, bool seamEnabled) =>
        new(environment, seamEnabled);

    [Fact]
    public async Task Proposing_files_a_pending_entry_the_framework_owns()
    {
        using var host = Host("Development", seamEnabled: true);
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync("/api/dev/propose", new { sessionId = "seam-test" });
        response.EnsureSuccessStatusCode();
        var filed = await response.Content.ReadFromJsonAsync<ProposeResponse>(Json);

        Assert.NotNull(filed);
        Assert.NotEqual(Guid.Empty, filed.DocketId);

        // The entry is the framework's, not the seam's: read it straight off the docket store the
        // review gate filed it into, and check the shape the gate itself stamps.
        var store = host.Services.GetRequiredService<IDocketStore>();
        var entry = await store.GetDocketEntryAsync(filed.DocketId, CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal(ReviewStatus.Pending, entry.Status);
        Assert.Equal("seam-test", entry.SessionId);
        Assert.Equal("request_leave", entry.OperationType);
        Assert.Equal("LeaveRequest", entry.Envelope.EntityType);
        Assert.Null(entry.Envelope.EntityId);

        // The seam's canned proposal leaves the employee blank on purpose, so the mandatory-field
        // gate and the reviewer's picker have something to act on.
        var employee = entry.Envelope.Fields.Single(f => f.Name == "Employee");
        Assert.True(employee.IsMandatory);
        Assert.Equal(ProvenanceSource.Empty, employee.Provenance.Current.Source);
    }

    [Fact]
    public async Task An_unreviewed_entry_expires_as_state_not_as_a_timeout()
    {
        using var host = Host("Development", seamEnabled: true);
        using var client = host.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/dev/propose", new { sessionId = "expiry-test", ttlSeconds = 1 });
        response.EnsureSuccessStatusCode();
        var filed = await response.Content.ReadFromJsonAsync<ProposeResponse>(Json);
        Assert.NotNull(filed);

        var pending = await client.GetFromJsonAsync<DocketResponse>(
            $"/api/dev/docket/{filed.DocketId}", Json, CancellationToken.None);
        Assert.Equal("Pending", pending?.Status);

        await Task.Delay(TimeSpan.FromSeconds(1.2), CancellationToken.None);

        // Drive the framework's own sweep rather than waiting out its 30-second timer. Nothing
        // about the transition is the seam's or the test's: the sweep reads the store, sees the
        // deadline has passed and writes Expired.
        var sweep = host.Services.GetServices<IHostedService>().OfType<DocketExpiryService>().Single();
        await sweep.ExpireOverdueAsync(CancellationToken.None);

        var expired = await client.GetFromJsonAsync<DocketResponse>(
            $"/api/dev/docket/{filed.DocketId}", Json, CancellationToken.None);
        Assert.Equal("Expired", expired?.Status);
    }

    [Fact]
    public async Task An_update_shaped_proposal_carries_the_entity_id()
    {
        using var host = Host("Development", seamEnabled: true);
        using var client = host.CreateClient();

        var store = host.Services.GetRequiredService<IDocketStore>();

        // Approve a create first so there is a row to amend.
        var recordId = await WriteALeaveRequestAsync(
            host, client, store, new Dictionary<string, string> { ["Employee"] = "Devon Park" });

        var updated = await client.PostAsJsonAsync(
            "/api/dev/propose",
            new { sessionId = "update-test", entityId = recordId });
        updated.EnsureSuccessStatusCode();
        var updatedEntry = await updated.Content.ReadFromJsonAsync<ProposeResponse>(Json);
        Assert.NotNull(updatedEntry);

        var updateEntry = await store.GetDocketEntryAsync(updatedEntry.DocketId, CancellationToken.None);
        Assert.NotNull(updateEntry);
        Assert.Equal("amend_leave", updateEntry.OperationType);
        Assert.Equal(
            recordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            updateEntry.Envelope.EntityId);
        Assert.All(updateEntry.Envelope.Fields, field => Assert.NotNull(field.PreviousValue));
        Assert.Equal("Devon Park", updateEntry.Envelope.Fields.Single(f => f.Name == "Employee").PreviousValue);
    }

    [Fact]
    public async Task An_update_swears_only_what_the_caller_stated_and_reads_the_rest_off_the_row()
    {
        using var host = Host("Development", seamEnabled: true);
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<IDocketStore>();

        // A reason nothing like the seam's canned one, so "the update left the row alone" and "the
        // update re-stated the canned default" cannot be mistaken for each other.
        const string rowReason = "Original reason, stated when the row was created.";
        var recordId = await WriteALeaveRequestAsync(
            host,
            client,
            store,
            new Dictionary<string, string> { ["Employee"] = "Devon Park", ["Reason"] = rowReason });

        var updated = await client.PostAsJsonAsync(
            "/api/dev/propose",
            new
            {
                sessionId = "update-provenance",
                entityId = recordId,
                overrides = new Dictionary<string, string> { ["EndDate"] = "2026-12-25" },
            });
        updated.EnsureSuccessStatusCode();
        var filed = await updated.Content.ReadFromJsonAsync<ProposeResponse>(Json);
        Assert.NotNull(filed);

        var entry = await store.GetDocketEntryAsync(filed.DocketId, CancellationToken.None);
        Assert.NotNull(entry);

        // The one field the caller named is the one field sworn UserStated.
        var endDate = entry.Envelope.Fields.Single(f => f.Name == "EndDate");
        Assert.Equal("2026-12-25", endDate.Value);
        Assert.Equal("2026-11-06", endDate.PreviousValue);
        Assert.Equal(ProvenanceSource.UserStated, endDate.Provenance.Current.Source);

        // Every other field is still proposed — an affidavit states the whole row — but it names
        // the database as its source, and says which record it read.
        foreach (var field in entry.Envelope.Fields.Where(f => f.Name != "EndDate"))
        {
            Assert.Equal(ProvenanceSource.External, field.Provenance.Current.Source);
            Assert.Equal(field.PreviousValue, field.Value);
            Assert.Contains(
                "external-ref",
                field.Provenance.Current.Evidence ?? string.Empty,
                StringComparison.Ordinal);
        }

        // Reason in particular: the canned create default is not written over the row's own text.
        Assert.Equal(rowReason, entry.Envelope.Fields.Single(f => f.Name == "Reason").Value);
    }

    /// <summary>
    /// Files a create through the seam and executes it, so a real row exists to update. Returns the
    /// row's id.
    /// </summary>
    private static async Task<int> WriteALeaveRequestAsync(
        QuickstartHostFactory host,
        HttpClient client,
        IDocketStore store,
        Dictionary<string, string> overrides)
    {
        var created = await client.PostAsJsonAsync(
            "/api/dev/propose", new { sessionId = "seed", overrides });
        created.EnsureSuccessStatusCode();
        var createdEntry = await created.Content.ReadFromJsonAsync<ProposeResponse>(Json);
        Assert.NotNull(createdEntry);

        var entry = await store.GetDocketEntryAsync(createdEntry.DocketId, CancellationToken.None);
        Assert.NotNull(entry);

        using var scope = host.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IWriteExecutor>();
        var recordId = await executor.ExecuteAsync(
            entry.Envelope, entry.Amendments, CancellationToken.None);
        Assert.NotNull(recordId);

        return int.Parse(recordId, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public async Task The_seam_is_a_plain_404_unless_both_conditions_hold(string environment, bool seamEnabled)
    {
        using var host = Host(environment, seamEnabled);
        using var client = host.CreateClient();

        var propose = await client.PostAsJsonAsync("/api/dev/propose", new { sessionId = "gate-test" });
        Assert.Equal(HttpStatusCode.NotFound, propose.StatusCode);

        var read = await client.GetAsync($"/api/dev/docket/{Guid.NewGuid()}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    private sealed record ProposeResponse(string SessionId, Guid DocketId);

    private sealed record DocketResponse(
        string Status, DateTimeOffset ExpiresAt, Dictionary<string, object?>? Amendments);
}
