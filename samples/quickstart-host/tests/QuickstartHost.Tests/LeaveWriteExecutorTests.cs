namespace QuickstartHost.Tests;

using Affiant.Abstractions.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuickstartHost.Agent;
using QuickstartHost.Data;
using QuickstartHost.Execution;
using Xunit;

/// <summary>
/// What the executor does with a field the record proposes with no value. There are two ways a
/// field arrives like that, and they mean opposite things: a reviewer cleared it, or nobody ever
/// sourced it. The reviewer's act is on the field's current tag, so the executor can tell them
/// apart — and must, because a host that copies this executor and the shipped
/// <c>SchemaDrivenAffidavitProjection</c> gets an unsourced field on every update the model said
/// nothing about.
/// </summary>
public sealed class LeaveWriteExecutorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public LeaveWriteExecutorTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<HrDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HrDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task A_field_a_reviewer_cleared_blanks_the_column()
    {
        var recordId = SeedLeaveRequest();
        var cleared = AffidavitAmendments.AmendmentTag(
            cleared: true,
            entryId: Guid.NewGuid(),
            decisionAt: DateTimeOffset.UnixEpoch,
            reviewerId: "reviewer-1",
            conversationTurn: null);

        await ExecuteAsync(recordId, new AffidavitField(
            "Reason", null, "Family visit overseas.", ProvenanceChain.From(cleared), IsMandatory: true));

        Assert.Equal(string.Empty, Read(recordId).Reason);
    }

    [Fact]
    public async Task A_field_nobody_sourced_leaves_the_column_alone()
    {
        var recordId = SeedLeaveRequest();

        // What SchemaDrivenAffidavitProjection swears for a field with no chain: present, Empty,
        // no value and no binding — not an instruction to blank anything.
        await ExecuteAsync(recordId, new AffidavitField(
            "Reason", null, null, ProvenanceChain.From(ProvenanceTag.Empty), IsMandatory: true));

        Assert.Equal("Family visit overseas.", Read(recordId).Reason);
    }

    private async Task ExecuteAsync(int recordId, params AffidavitField[] fields)
    {
        using var scope = _services.CreateScope();
        var executor = new LeaveWriteExecutor(scope.ServiceProvider.GetRequiredService<HrDbContext>());

        await executor.ExecuteAsync(
            Affidavit.Create(
                operationType: LeaveProposalBuilder.UpdateOperation,
                entityType: LeaveTaskInferenceStrategy.LeaveRequestEntity,
                entityId: recordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                fields: fields,
                warnings: []),
            amendments: null,
            CancellationToken.None);
    }

    private LeaveRequest Read(int recordId)
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<HrDbContext>()
            .LeaveRequests.AsNoTracking().Single(r => r.Id == recordId);
    }

    private int SeedLeaveRequest()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HrDbContext>();
        var record = new LeaveRequest
        {
            Employee = "Amara Silva",
            StartDate = new DateOnly(2026, 11, 2),
            EndDate = new DateOnly(2026, 11, 6),
            LeaveType = "Annual",
            Days = 5,
            Reason = "Family visit overseas.",
            Status = "Submitted",
        };
        db.LeaveRequests.Add(record);
        db.SaveChanges();
        return record.Id;
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }
}
