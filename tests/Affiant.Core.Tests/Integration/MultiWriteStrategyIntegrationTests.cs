namespace Affiant.Core.Tests.Integration;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Story 20.3 regression guard: verifies that TaskInferenceStep.ExecuteAsync uses the
/// strategy supplied as a parameter, not a singleton-bound ITaskInferenceStrategy from DI.
///
/// Before Story 20.3, TaskInferenceStep took ITaskInferenceStrategy in its constructor.
/// Multi-write hosts (e.g., HR Portal with Leave, PersonalInfo, and ExpenseReport write tools)
/// could only bind a single strategy to the DI container, causing all write tools to use the
/// same strategy — the singleton-bound fallback.
///
/// After Story 20.3, the strategy is passed per-invocation to ExecuteAsync. This test proves
/// that when three different strategies are used, each invocation respects its own strategy's
/// field schema and does NOT cross-contaminate with another strategy's fields.
/// </summary>
public sealed class MultiWriteStrategyIntegrationTests
{
    // A JSON payload containing ALL fields from all three synthetic strategies.
    // When passed to ExecuteAsync with a specific strategy, only THAT strategy's fields
    // should appear in the merged result — never another strategy's fields.
    private static readonly JsonElement AllStrategiesJson = JsonDocument.Parse("""
        {
            "StartDate":    { "value": "2026-07-01", "confidence": 0.9 },
            "EndDate":      { "value": "2026-07-05", "confidence": 0.9 },
            "LeaveType":    { "value": "Annual",     "confidence": 0.9 },
            "FieldName":    { "value": "Email",      "confidence": 0.9 },
            "NewValue":     { "value": "a@b.com",    "confidence": 0.9 },
            "ExpenseDate":  { "value": "2026-06-01", "confidence": 0.9 },
            "Amount":       { "value": "150.00",     "confidence": 0.9 },
            "Category":     { "value": "Meals",      "confidence": 0.9 }
        }
        """).RootElement;

    [Fact]
    public async Task LeaveStrategy_MergesOnlyLeaveFields_NotPersonalInfoOrExpenseFields()
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var strategy = new SyntheticLeaveStrategy();

        var result = await step.ExecuteAsync(strategy, AllStrategiesJson);

        Assert.True(result.MergedFields.ContainsKey("StartDate"), "StartDate must be in Leave result");
        Assert.True(result.MergedFields.ContainsKey("EndDate"), "EndDate must be in Leave result");
        Assert.True(result.MergedFields.ContainsKey("LeaveType"), "LeaveType must be in Leave result");

        Assert.False(result.MergedFields.ContainsKey("FieldName"),
            "FieldName (PersonalInfo field) must NOT appear in Leave merge");
        Assert.False(result.MergedFields.ContainsKey("ExpenseDate"),
            "ExpenseDate (ExpenseReport field) must NOT appear in Leave merge");

        var entity = fabric.GetByKey("SyntheticLeave");
        Assert.NotNull(entity);
        Assert.Equal("SyntheticLeave", entity.EntityType);
    }

    [Fact]
    public async Task PersonalInfoStrategy_MergesOnlyPersonalInfoFields_NotLeaveOrExpenseFields()
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var strategy = new SyntheticPersonalInfoStrategy();

        var result = await step.ExecuteAsync(strategy, AllStrategiesJson);

        Assert.True(result.MergedFields.ContainsKey("FieldName"), "FieldName must be in PersonalInfo result");
        Assert.True(result.MergedFields.ContainsKey("NewValue"), "NewValue must be in PersonalInfo result");

        Assert.False(result.MergedFields.ContainsKey("StartDate"),
            "StartDate (Leave field) must NOT appear in PersonalInfo merge");
        Assert.False(result.MergedFields.ContainsKey("ExpenseDate"),
            "ExpenseDate (ExpenseReport field) must NOT appear in PersonalInfo merge");

        var entity = fabric.GetByKey("SyntheticPersonalInfo");
        Assert.NotNull(entity);
        Assert.Equal("SyntheticPersonalInfo", entity.EntityType);
    }

    [Fact]
    public async Task ExpenseReportStrategy_MergesOnlyExpenseFields_NotLeaveOrPersonalInfoFields()
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var strategy = new SyntheticExpenseReportStrategy();

        var result = await step.ExecuteAsync(strategy, AllStrategiesJson);

        Assert.True(result.MergedFields.ContainsKey("ExpenseDate"), "ExpenseDate must be in ExpenseReport result");
        Assert.True(result.MergedFields.ContainsKey("Amount"), "Amount must be in ExpenseReport result");
        Assert.True(result.MergedFields.ContainsKey("Category"), "Category must be in ExpenseReport result");

        Assert.False(result.MergedFields.ContainsKey("StartDate"),
            "StartDate (Leave field) must NOT appear in ExpenseReport merge");
        Assert.False(result.MergedFields.ContainsKey("FieldName"),
            "FieldName (PersonalInfo field) must NOT appear in ExpenseReport merge");

        var entity = fabric.GetByKey("SyntheticExpenseReport");
        Assert.NotNull(entity);
        Assert.Equal("SyntheticExpenseReport", entity.EntityType);
    }

    /// <summary>
    /// Verifies the negative path: if the same JSON is processed with the wrong strategy
    /// (Leave used instead of PersonalInfo), the PersonalInfo-specific field FieldName is absent
    /// and Leave-specific fields appear instead. This is the pre-fix bug reproduced.
    /// </summary>
    [Fact]
    public async Task WrongStrategy_ProducesWrongSchema_NegativePath()
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var wrongStrategy = new SyntheticLeaveStrategy(); // intentionally wrong for PersonalInfo

        var result = await step.ExecuteAsync(wrongStrategy, AllStrategiesJson);

        // With the wrong (Leave) strategy, PersonalInfo fields are NOT present
        Assert.False(result.MergedFields.ContainsKey("FieldName"),
            "PersonalInfo field FieldName must NOT appear when Leave strategy is used — confirms gate is load-bearing");
        Assert.False(result.MergedFields.ContainsKey("NewValue"),
            "PersonalInfo field NewValue must NOT appear when Leave strategy is used");

        // And Leave fields ARE present (wrong schema produced)
        Assert.True(result.MergedFields.ContainsKey("StartDate"),
            "Leave field StartDate must be present when Leave strategy is (wrongly) used");
    }

    // ── Synthetic strategies (domain-agnostic; model HR Portal's three write-tool schemas) ──

    private sealed class SyntheticLeaveStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "SyntheticLeave";
        public IReadOnlyList<TaskInferenceField> Fields =>
        [
            new TaskInferenceField("StartDate", "string", "Leave start date"),
            new TaskInferenceField("EndDate", "string", "Leave end date"),
            new TaskInferenceField("LeaveType", "string", "Type of leave",
                Enum: ["Annual", "Sick", "Personal"]),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class SyntheticPersonalInfoStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "SyntheticPersonalInfo";
        public IReadOnlyList<TaskInferenceField> Fields =>
        [
            new TaskInferenceField("FieldName", "string", "Field to update", Enum: ["Email"]),
            new TaskInferenceField("NewValue", "string", "New field value"),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class SyntheticExpenseReportStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "SyntheticExpenseReport";
        public IReadOnlyList<TaskInferenceField> Fields =>
        [
            new TaskInferenceField("ExpenseDate", "string", "Date of expense"),
            new TaskInferenceField("Amount", "string", "Expense amount"),
            new TaskInferenceField("Category", "string", "Expense category",
                Enum: ["Travel", "Meals", "Lodging", "Supplies", "Other"]),
        ];
        public double? MinimumConfidenceThreshold => null;
    }
}
