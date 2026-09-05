namespace QuickstartHost.Tests;

using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuickstartHost.Agent;
using QuickstartHost.Data;
using QuickstartHost.Projection;
using Xunit;

/// <summary>
/// The behaviour this sample exists to show: an update-shaped write carries the entity's id and
/// each field's current database value, and a create carries neither. Both come from the host's own
/// projection — the framework's default hard-codes both to null. The confidence a reviewer is shown
/// is the minimum over every proposed field, which is the other reason this projection exists.
/// </summary>
public sealed class LeaveAffidavitProjectionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public LeaveAffidavitProjectionTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<HrDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<LeaveTaskInferenceStrategy>();
        services.AddSingleton<LeaveAffidavitProjection>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<HrDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void Create_carries_no_entity_id_and_no_previous_values()
    {
        var projection = _services.GetRequiredService<LeaveAffidavitProjection>();
        var fabric = FabricWith(
            entityId: null,
            stated: new Dictionary<string, string>
            {
                ["Employee"] = "Amara Silva",
                ["StartDate"] = "2026-11-02",
                ["EndDate"] = "2026-11-06",
                ["LeaveType"] = "Annual",
                ["Days"] = "5",
                ["Reason"] = "Family visit overseas.",
            });

        var affidavit = projection.Project(fabric, LeaveProposalBuilder.CreateOperation, []);

        Assert.Null(affidavit.EntityId);
        Assert.All(affidavit.Fields, field => Assert.Null(field.PreviousValue));
        Assert.All(affidavit.Fields, field =>
            Assert.Equal(ProvenanceSource.UserStated, field.Provenance.Current.Source));
        Assert.Equal("2026-11-06", Field(affidavit, "EndDate").Value);
    }

    [Fact]
    public void Update_carries_the_entity_id_and_every_field_previous_value_from_the_database()
    {
        var recordId = SeedLeaveRequest();
        var projection = _services.GetRequiredService<LeaveAffidavitProjection>();

        // Only the end date is stated — exactly what amend_leave sends.
        var fabric = FabricWith(
            entityId: recordId,
            stated: new Dictionary<string, string> { ["EndDate"] = "2026-11-13" });

        var affidavit = projection.Project(fabric, LeaveProposalBuilder.UpdateOperation, []);

        Assert.Equal(recordId.ToString(), affidavit.EntityId);

        var endDate = Field(affidavit, "EndDate");
        Assert.Equal("2026-11-13", endDate.Value);
        Assert.Equal("2026-11-06", endDate.PreviousValue);
        Assert.Equal(ProvenanceSource.UserStated, endDate.Provenance.Current.Source);

        // A field nobody asked to change is still proposed — an affidavit states the whole row —
        // and names the database as its source rather than claiming a user said it.
        var employee = Field(affidavit, "Employee");
        Assert.Equal("Amara Silva", employee.Value);
        Assert.Equal("Amara Silva", employee.PreviousValue);
        Assert.Equal(ProvenanceSource.External, employee.Provenance.Current.Source);

        Assert.All(affidavit.Fields, field => Assert.NotNull(field.PreviousValue));
    }

    [Fact]
    public void Field_metadata_comes_from_the_schema_so_a_reviewer_ui_can_render_it()
    {
        var projection = _services.GetRequiredService<LeaveAffidavitProjection>();
        var affidavit = projection.Project(
            FabricWith(entityId: null, stated: new Dictionary<string, string> { ["LeaveType"] = "Sick" }),
            LeaveProposalBuilder.CreateOperation,
            []);

        var leaveType = Field(affidavit, "LeaveType");
        Assert.Equal(AffidavitFieldKind.Enum, leaveType.Kind);
        Assert.Equal(["Annual", "Sick", "Personal"], leaveType.AllowedValues);

        var startDate = Field(affidavit, "StartDate");
        Assert.Equal(AffidavitFieldKind.Date, startDate.Kind);
        Assert.Equal(@"^\d{4}-\d{2}-\d{2}$", startDate.Pattern);

        // Rule 7 (nothing is omitted): a field with nothing behind it is tagged Empty, never
        // dropped. The numbered rules are defined in docs/affiant-framework-specification.md §6.
        Assert.Equal(ProvenanceSource.Empty, Field(affidavit, "Reason").Provenance.Current.Source);
        Assert.Contains(affidavit.Warnings, w => w.Contains("Reason", StringComparison.Ordinal));
    }

    [Fact]
    public void One_unsourced_mandatory_field_takes_the_aggregate_confidence_to_zero()
    {
        var projection = _services.GetRequiredService<LeaveAffidavitProjection>();

        // Everything the development seam's canned create states except the employee — the case it
        // stages on purpose. Five fields are UserStated at 1.0 and one mandatory field has nothing
        // behind it at all; a mean over only the sourced five reports 1.00 on the card.
        var affidavit = projection.Project(
            FabricWith(
                entityId: null,
                stated: new Dictionary<string, string>
                {
                    ["StartDate"] = "2026-11-02",
                    ["EndDate"] = "2026-11-06",
                    ["LeaveType"] = "Annual",
                    ["Days"] = "5",
                    ["Reason"] = "Family visit overseas.",
                }),
            LeaveProposalBuilder.CreateOperation,
            []);

        var employee = Field(affidavit, "Employee");
        Assert.True(employee.IsMandatory);
        Assert.Equal(ProvenanceSource.Empty, employee.Provenance.Current.Source);

        Assert.Equal(0f, affidavit.AggregateConfidence);

        // The minimum across the fields that do have a source, and how many have none, belong
        // beside that number. The beta.1 Affidavit record has nowhere to carry them, so the
        // projection states them where the Evidence Card renders them.
        Assert.Contains(
            affidavit.Warnings,
            w => w.Contains("aggregate 0.00", StringComparison.Ordinal)
                && w.Contains("populated 1.00", StringComparison.Ordinal)
                && w.Contains("1 field(s) with no source", StringComparison.Ordinal));
    }

    [Fact]
    public void Aggregate_confidence_is_only_zero_because_of_the_empty_field()
    {
        var projection = _services.GetRequiredService<LeaveAffidavitProjection>();

        var affidavit = projection.Project(
            FabricWith(
                entityId: null,
                stated: new Dictionary<string, string>
                {
                    ["Employee"] = "Amara Silva",
                    ["StartDate"] = "2026-11-02",
                    ["EndDate"] = "2026-11-06",
                    ["LeaveType"] = "Annual",
                    ["Days"] = "5",
                    ["Reason"] = "Family visit overseas.",
                }),
            LeaveProposalBuilder.CreateOperation,
            []);

        Assert.Equal(1f, affidavit.AggregateConfidence);
        Assert.Contains(
            affidavit.Warnings,
            w => w.Contains("0 field(s) with no source", StringComparison.Ordinal));
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

    private static ContextFabric FabricWith(int? entityId, Dictionary<string, string> stated)
    {
        var fabric = new ContextFabric();
        var fields = stated.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);
        if (entityId is { } id)
            fields[LeaveTaskInferenceStrategy.EntityIdField] = id;

        fabric.Upsert(new EntityRef(
            LeaveTaskInferenceStrategy.LeaveRequestEntity,
            LeaveTaskInferenceStrategy.LeaveRequestEntity,
            "Leave request",
            fields));

        foreach (var name in stated.Keys)
        {
            fabric.SetFieldChain(name, ProvenanceChain.From(
                ProvenanceTag.FromUser(name, new ProvenanceBinding.FormInput(new FormInputRef(name)))));
        }

        return fabric;
    }

    private static AffidavitField Field(Affidavit affidavit, string name) =>
        affidavit.Fields.Single(f => f.Name == name);

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }
}
