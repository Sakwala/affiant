namespace QuickstartHost.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuickstartHost.Agent;
using QuickstartHost.Data;
using QuickstartHost.Projection;
using Xunit;

/// <summary>
/// How this sample grades the values a proposal carries. The rule is PV-3's: a grade above
/// <c>Conversation</c> is a claim about a person's act, and a write tool's arguments are a model's
/// output — so the model path can reach <c>Inferred</c> and no further, and only the development
/// seam, where a person writes the values into the request, mints <c>UserStated</c>.
/// </summary>
public sealed class LeaveProposalBuilderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public LeaveProposalBuilderTests()
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

    private LeaveProposalBuilder Builder() =>
        new([_services.GetRequiredService<LeaveAffidavitProjection>()]);

    private static readonly Dictionary<string, string> ModelExtracted = new(StringComparer.Ordinal)
    {
        ["Employee"] = "Amara Silva",
        ["StartDate"] = "2026-11-02",
        ["EndDate"] = "2026-11-06",
        ["LeaveType"] = "Annual",
        ["Days"] = "5",
        ["Reason"] = "Family visit overseas.",
    };

    [Fact]
    public void A_model_s_arguments_are_graded_Inferred_and_bind_to_nothing()
    {
        var affidavit = Builder().BuildCreate(ModelExtracted, ProposalOrigin.ModelArguments);

        foreach (var name in ModelExtracted.Keys)
        {
            var tag = affidavit.Fields.Single(f => f.Name == name).Provenance.Current;
            Assert.Equal(ProvenanceSource.Inferred, tag.Source);
            Assert.Equal(LeaveProposalBuilder.ModelArgumentConfidence, tag.Confidence);

            // Nothing to point an auditor at: this host establishes nothing about where in the turn
            // the value appeared, and a binding it cannot resolve is worse than none (PV-2).
            Assert.Null(tag.Binding);
        }
    }

    [Fact]
    public void A_person_s_own_values_are_UserStated_and_bind_to_the_request_they_arrived_in()
    {
        var affidavit = Builder().BuildCreate(ModelExtracted, ProposalOrigin.DevSeamRequest);

        var reason = affidavit.Fields.Single(f => f.Name == "Reason").Provenance.Current;
        Assert.Equal(ProvenanceSource.UserStated, reason.Source);

        var binding = Assert.IsType<ProvenanceBinding.FormInput>(reason.Binding);
        Assert.Equal($"{LeaveProposalBuilder.DevSeamSurface}#Reason", binding.Ref.Field);
    }

    [Fact]
    public void An_update_grades_the_model_s_own_argument_and_reads_the_rest_off_the_row()
    {
        var recordId = SeedLeaveRequest();

        var affidavit = Builder().BuildUpdate(
            recordId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["EndDate"] = "2026-11-13" },
            ProposalOrigin.ModelArguments);

        var endDate = affidavit.Fields.Single(f => f.Name == "EndDate").Provenance.Current;
        Assert.Equal(ProvenanceSource.Inferred, endDate.Source);
        Assert.Null(endDate.Binding);

        // The unchanged fields are the record's, and say so — the contrast the sample exists to
        // show survives the regrading of the changed one.
        foreach (var field in affidavit.Fields.Where(f => f.Name != "EndDate"))
            Assert.Equal(ProvenanceSource.External, field.Provenance.Current.Source);
    }

    [Fact]
    public async Task The_write_tool_swears_nothing_UserStated()
    {
        // The whole of #110 in one assertion, on the tool a model actually calls. Provenance
        // sources travel PascalCase on the wire (AffiantJson, SR-3), so the proposal's own JSON is
        // where the claim is either made or not made.
        var json = await new RequestLeavePlugin(Builder()).RequestLeaveAsync(
            employee: "Amara Silva",
            startDate: "2026-11-02",
            endDate: "2026-11-06",
            leaveType: "Annual",
            days: 5,
            reason: "Family visit overseas.");

        Assert.DoesNotContain("UserStated", json, StringComparison.Ordinal);
        Assert.Contains("Inferred", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_update_tool_swears_nothing_UserStated()
    {
        var recordId = SeedLeaveRequest();

        var json = await new AmendLeavePlugin(Builder()).AmendLeaveAsync(recordId, "2026-11-13");

        Assert.DoesNotContain("UserStated", json, StringComparison.Ordinal);
        Assert.Contains("Inferred", json, StringComparison.Ordinal);
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
