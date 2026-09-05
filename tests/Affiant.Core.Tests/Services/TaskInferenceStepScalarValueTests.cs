namespace Affiant.Core.Tests.Services;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Regression coverage for TaskInferenceStep.ExecuteAsync reading "value" sub-properties
/// across JSON scalar kinds. Structured-output models emit numeric and boolean fields as
/// native JSON numbers/booleans; calling GetString() on those previously threw
/// InvalidOperationException, which aborted the entire merge loop and dropped every field.
///
/// <para>
/// A value keeps the JSON type the port reported it as: a number stays a number and a boolean stays
/// a boolean, all the way onto the card. The field's <c>kind</c> is a rendering hint for a reviewer
/// surface, not a licence to re-type the value, and a card showing <c>"40"</c> where the port said
/// <c>40</c> shows a different value from the one the record swears to (AF-1, SR-2).
/// </para>
/// </summary>
public class TaskInferenceStepScalarValueTests
{
    private sealed class FieldsStrategy(params TaskInferenceField[] fields) : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";
        public IReadOnlyList<TaskInferenceField> Fields { get; } = fields;
        public double? MinimumConfidenceThreshold => null;
    }

    private static (TaskInferenceStep step, ContextFabric fabric) Build()
    {
        var fabric = new ContextFabric();
        return (new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance), fabric);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Numeric_value_is_read_without_throwing()
    {
        var (step, fabric) = Build();
        var json = Parse("""{ "EstimatedHours": { "value": 4, "confidence": 0.8 } }""");

        var result = await step.ExecuteAsync(new FieldsStrategy(new TaskInferenceField("EstimatedHours", "number", "Estimated hours")), json);

        Assert.True(result.MergedFields["EstimatedHours"].Merged);
        Assert.Equal(4, fabric.GetByKey("Thing")!.Fields["EstimatedHours"]);
    }

    [Fact]
    public async Task Decimal_value_preserves_its_type()
    {
        var (step, fabric) = Build();
        var json = Parse("""{ "EstimatedHours": { "value": 4.5, "confidence": 0.8 } }""");

        await step.ExecuteAsync(new FieldsStrategy(new TaskInferenceField("EstimatedHours", "number", "Estimated hours")), json);

        Assert.Equal(4.5d, fabric.GetByKey("Thing")!.Fields["EstimatedHours"]);
    }

    [Fact]
    public async Task Boolean_value_is_read_as_a_boolean()
    {
        var (step, fabric) = Build();
        var json = Parse("""{ "Urgent": { "value": true, "confidence": 0.9 } }""");

        await step.ExecuteAsync(new FieldsStrategy(new TaskInferenceField("Urgent", "boolean", "Urgent flag")), json);

        Assert.Equal(true, fabric.GetByKey("Thing")!.Fields["Urgent"]);
    }

    [Fact]
    public async Task Mixed_scalar_kinds_all_merge_in_one_pass()
    {
        var (step, fabric) = Build();
        var json = Parse("""
            {
                "Title": { "value": "Replace landing gear tyre", "confidence": 0.95 },
                "EstimatedHours": { "value": 6, "confidence": 0.8 }
            }
            """);

        var result = await step.ExecuteAsync(
            new FieldsStrategy(
                new TaskInferenceField("Title", "string", "Title"),
                new TaskInferenceField("EstimatedHours", "number", "Estimated hours")),
            json);

        Assert.True(result.MergedFields["Title"].Merged);
        Assert.True(result.MergedFields["EstimatedHours"].Merged);
        Assert.Equal("Replace landing gear tyre", fabric.GetByKey("Thing")!.Fields["Title"]);
        Assert.Equal(6, fabric.GetByKey("Thing")!.Fields["EstimatedHours"]);
    }

    [Fact]
    public async Task Non_scalar_value_is_skipped_without_aborting_sibling_fields()
    {
        var (step, fabric) = Build();
        var json = Parse("""
            {
                "Tags": { "value": ["a", "b"], "confidence": 0.7 },
                "Title": { "value": "Survivor", "confidence": 0.9 }
            }
            """);

        var result = await step.ExecuteAsync(
            new FieldsStrategy(
                new TaskInferenceField("Tags", "array", "Tags"),
                new TaskInferenceField("Title", "string", "Title")),
            json);

        Assert.False(result.MergedFields.ContainsKey("Tags"));
        Assert.True(result.MergedFields["Title"].Merged);
        Assert.Equal("Survivor", fabric.GetByKey("Thing")!.Fields["Title"]);
    }
}
