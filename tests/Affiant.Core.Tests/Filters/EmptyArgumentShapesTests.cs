namespace Affiant.Core.Tests.Filters;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// AF-1: an argument with no value is sworn <c>Empty</c> at confidence 0, whichever shape "no
/// value" arrives in.
/// </summary>
/// <remarks>
/// A model's tool call reaches the framework through an adapter, and what an adapter hands over
/// depends on how it parsed the JSON: Microsoft.Extensions.AI yields a C# <c>null</c>, another
/// deserializer yields a <see cref="JsonElement"/> whose kind is <c>Null</c>, and a data-layer
/// caller can hand over <see cref="DBNull"/>. All three say the same thing — the conversation said
/// nothing about this field — and a filter that tagged two of them <c>Conversation</c> at 0.9 would
/// swear that it had.
/// </remarks>
public sealed class EmptyArgumentShapesTests
{
    public static TheoryData<string, object?> NoValueShapes() => new()
    {
        { "a C# null", null },
        { "a JSON null", JsonDocument.Parse("""{"v":null}""").RootElement.GetProperty("v") },
        { "DBNull", DBNull.Value },
    };

    [Theory]
    [MemberData(nameof(NoValueShapes))]
    public async Task AnArgumentWithNoValue_IsNotTagged(string shape, object? value)
    {
        Assert.NotEmpty(shape);
        var fabric = new ContextFabric();
        await CaptureAsync(fabric, new Dictionary<string, object?> { ["reference"] = value });

        Assert.Null(fabric.GetFieldChain("reference"));
    }

    [Theory]
    [MemberData(nameof(NoValueShapes))]
    public async Task TheProjectionSwearsIt_Empty_AndTheThreeNumbersFollow(string shape, object? value)
    {
        Assert.NotEmpty(shape);
        var fabric = new ContextFabric();
        await CaptureAsync(
            fabric,
            new Dictionary<string, object?> { ["status"] = "Active", ["reference"] = value });

        var affidavit = Project(fabric);

        var reference = affidavit.Fields.Single(f => f.Name == "reference");
        Assert.Equal(ProvenanceSource.Empty, reference.Provenance.Current.Source);
        Assert.Equal(0f, reference.Provenance.Current.Confidence);
        Assert.Null(reference.Value);

        // AF-2: the aggregate is the minimum with an Empty field counting 0; the populated number is
        // the mean of the fields that do have a source; the count says how many swear to nothing.
        Assert.Equal(0f, affidavit.AggregateConfidence);
        Assert.Equal(0.9f, affidavit.PopulatedConfidence);
        Assert.Equal(1, affidavit.EmptyFieldCount);
    }

    [Fact]
    public async Task AnArgumentThatCarriesAValue_IsStillTagged()
    {
        var fabric = new ContextFabric();
        await CaptureAsync(fabric, new Dictionary<string, object?> { ["status"] = "Active" });

        var chain = Assert.IsType<ProvenanceChain>(fabric.GetFieldChain("status"));
        Assert.Equal(ProvenanceSource.Conversation, chain.Current.Source);
        Assert.Equal(0.9f, chain.Current.Confidence);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const string ToolName = "update_invoice";

    private static async Task CaptureAsync(ContextFabric fabric, Dictionary<string, object?> arguments)
    {
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor(
            ToolName, null, Operation.WriteUpdate, "Invoice", null));

        var services = new ServiceCollection().BuildServiceProvider();

        await new ToolArgumentCaptureFilter(
                fabric, registry, NullLogger<ToolArgumentCaptureFilter>.Instance)
            .OnToolInvocationAsync(
                new ToolInvocationContext
                {
                    FunctionName = ToolName,
                    PluginName = string.Empty,
                    Arguments = arguments,
                    Services = services,
                },
                _ => Task.CompletedTask);
    }

    private static Affidavit Project(ContextFabric fabric)
    {
        var strategy = new InvoiceStrategy();
        var values = new Dictionary<string, object> { ["status"] = "Active" };
        fabric.Upsert(new EntityRef(strategy.EntityName, strategy.EntityName, strategy.EntityName, values));

        return new SchemaDrivenAffidavitProjection(
                strategy,
                [],
                [],
                NullLogger<SchemaDrivenAffidavitProjection>.Instance,
                new Affiant.Core.Observability.InMemoryObservabilityEventStream<AffidavitEmittedEvent>())
            .Project(fabric, "WriteUpdate", [], entityId: "invoice-1");
    }

    private sealed class InvoiceStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Invoice";

        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("status", "string", "status", null, null, null, true),
            new("reference", "string", "reference", null, null, null, false),
        ];

        public double? MinimumConfidenceThreshold => null;
    }
}
