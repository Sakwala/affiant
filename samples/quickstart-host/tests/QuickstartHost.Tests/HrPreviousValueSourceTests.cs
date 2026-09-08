namespace QuickstartHost.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QuickstartHost.Agent;
using QuickstartHost.Data;
using QuickstartHost.Review;
using Xunit;

/// <summary>
/// What the host's previous-value source answers has to be keyed the way a projection asks. The
/// framework's own <c>SchemaDrivenAffidavitProjection</c> looks each field up by the strategy's
/// declared <c>TaskInferenceField.Name</c> on an ordinal dictionary, so this sample is exercised
/// against that projection rather than against the sample's own — which supplies previous values
/// itself and would mask a key that never matches.
/// </summary>
public sealed class HrPreviousValueSourceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public HrPreviousValueSourceTests()
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
    public async Task Every_key_is_a_field_the_strategy_declares()
    {
        var recordId = SeedLeaveRequest();
        var source = new HrPreviousValueSource(
            _services.GetRequiredService<IServiceScopeFactory>());

        var previous = await source.GetPreviousValuesAsync(
            LeaveTaskInferenceStrategy.LeaveRequestEntity,
            recordId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CancellationToken.None);

        Assert.NotNull(previous);

        var declared = new LeaveTaskInferenceStrategy().Fields.Select(f => f.Name).ToArray();
        Assert.All(previous.Keys, key => Assert.Contains(key, declared));
    }

    [Fact]
    public async Task The_shipped_projection_finds_a_previous_value_for_every_field()
    {
        var recordId = SeedLeaveRequest();
        var strategy = new LeaveTaskInferenceStrategy();
        var projection = new SchemaDrivenAffidavitProjection(
            strategy,
            [],
            [],
            NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            new InMemoryObservabilityEventStream<AffidavitEmittedEvent>(),
            [new HrPreviousValueSource(_services.GetRequiredService<IServiceScopeFactory>())]);

        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef(
            LeaveTaskInferenceStrategy.LeaveRequestEntity,
            LeaveTaskInferenceStrategy.LeaveRequestEntity,
            "Leave request",
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["EndDate"] = "2026-11-13",
            }));
        fabric.SetFieldChain("EndDate", ProvenanceChain.From(
            ProvenanceTag.FromUser("EndDate", new ProvenanceBinding.FormInput(new FormInputRef("EndDate")))));

        var affidavit = projection.Project(
            fabric,
            LeaveProposalBuilder.UpdateOperation,
            [],
            recordId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Without a matching key every one of these is null and the reviewer sees no before/after.
        Assert.All(affidavit.Fields, field => Assert.NotNull(field.PreviousValue));
        Assert.Equal("2026-11-06", affidavit.Fields.Single(f => f.Name == "EndDate").PreviousValue);
        Assert.Equal("Amara Silva", affidavit.Fields.Single(f => f.Name == "Employee").PreviousValue);
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
