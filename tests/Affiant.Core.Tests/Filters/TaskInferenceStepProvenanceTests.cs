namespace Affiant.Core.Tests.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// What the implementation's own inference step is allowed to put on the record: the grade it mints,
/// the range a model-reported confidence lands in, and the merge it applies.
/// </summary>
public class TaskInferenceStepProvenanceTests
{
    private sealed class WidgetStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
            [new("Colour", "string", "Colour of the widget")];
        public double? MinimumConfidenceThreshold => null;
    }

    private static (ContextFabric Fabric, TaskInferenceStep Step) Build()
    {
        var fabric = new ContextFabric();
        return (fabric, new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance));
    }

    private static JsonElement Output(string colour, string confidence) =>
        JsonDocument
            .Parse("{\"Colour\": {\"value\": \"" + colour + "\", \"confidence\": " + confidence + "}}")
            .RootElement;

    [Fact]
    public async Task TheInferenceStep_MintsInferred_NeverUserStated()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new WidgetStrategy(), Output("red", "0.7"));

        var tag = fabric.GetFieldChain("Colour")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.NotEqual(ProvenanceSource.UserStated, tag.Source);
        Assert.NotEqual(ProvenanceSource.External, tag.Source);
        Assert.NotEqual(ProvenanceSource.Computed, tag.Source);
    }

    [Fact]
    public async Task AModelReportingMoreThanOne_CannotMintATagOutsideTheRange()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new WidgetStrategy(), Output("red", "1.4"));

        Assert.Equal(1.0f, fabric.GetFieldChain("Colour")!.Current.Confidence);
    }

    [Fact]
    public async Task AModelReportingLessThanZero_CannotMintATagOutsideTheRange()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new WidgetStrategy(), Output("red", "-0.5"));

        Assert.Equal(0f, fabric.GetFieldChain("Colour")!.Current.Confidence);
    }

    [Fact]
    public async Task AnInferenceLosesToAMoreConfidentIncumbent_AndIsPreservedInTheChain()
    {
        var (fabric, step) = Build();
        var incumbent = new ProvenanceTag(ProvenanceSource.External, 0.95f, "the system of record", null);
        fabric.SetFieldChain("Colour", ProvenanceChain.From(incumbent));

        var result = await step.ExecuteAsync(new WidgetStrategy(), Output("red", "0.4"));

        var chain = fabric.GetFieldChain("Colour")!;
        Assert.Equal(incumbent, chain.Current);
        Assert.Contains(chain.Prior, t => t.Source == ProvenanceSource.Inferred);
        Assert.False(result.MergedFields["Colour"].Merged);
    }

    [Fact]
    public async Task AnInferenceBeatsALessConfidentIncumbent_AndTheLoserIsPreserved()
    {
        var (fabric, step) = Build();
        var incumbent = new ProvenanceTag(ProvenanceSource.Default, 0.3f, "a fallback", null);
        fabric.SetFieldChain("Colour", ProvenanceChain.From(incumbent));

        var result = await step.ExecuteAsync(new WidgetStrategy(), Output("red", "0.8"));

        var chain = fabric.GetFieldChain("Colour")!;
        Assert.Equal(ProvenanceSource.Inferred, chain.Current.Source);
        Assert.Contains(incumbent, chain.Prior);
        Assert.True(result.MergedFields["Colour"].Merged);
    }
}
