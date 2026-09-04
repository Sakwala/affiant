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
/// AF-1: an argument with no value proposes nothing, whichever shape "no value" arrives in, and the
/// field it names is sworn <c>Empty</c> at confidence 0.
/// </summary>
/// <remarks>
/// A model's tool call reaches the framework through an adapter, and what an adapter hands over
/// depends on how it parsed the JSON: Microsoft.Extensions.AI yields a C# <c>null</c>, another
/// deserializer yields a <see cref="JsonElement"/> whose kind is <c>Null</c>, and a data-layer
/// caller can hand over <see cref="DBNull"/>. All three say the same thing — nothing was proposed
/// for this field — so none of them reaches the record as a value.
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
    public async Task AnArgumentWithNoValue_ProposesNothing(string shape, object? value)
    {
        Assert.NotEmpty(shape);
        var fabric = new ContextFabric();
        await CaptureAsync(fabric, new Dictionary<string, object?> { ["reference"] = value });

        Assert.Null(fabric.GetFieldChain("reference"));
        Assert.False(fabric.GetByKey("Invoice")?.Fields.ContainsKey("reference") ?? false);
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

        // What the host's inference port would say about the one field that has anything behind it.
        // The capture mints no tag of its own (PV-1: an argument is a proposal, not evidence), so
        // without this the record swears to nothing at all and the numbers below say so.
        fabric.SetFieldChain(
            "status",
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Conversation, "status", 0.9f)));

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

    /// <summary>
    /// An argument that carries a value proposes that value — and nothing more. PV-1: what the
    /// model wrote is not evidence about where the value came from, so the capture mints no tag;
    /// what swears for the field is an interceptor or the host's inference port, and where neither
    /// speaks the field is sworn Empty.
    /// </summary>
    [Fact]
    public async Task AnArgumentThatCarriesAValue_IsProposed_AndSwornByNothing()
    {
        var fabric = new ContextFabric();
        await CaptureAsync(fabric, new Dictionary<string, object?> { ["status"] = "Active" });

        Assert.Equal("Active", fabric.GetByKey("Invoice")!.Fields["status"]);
        Assert.Null(fabric.GetFieldChain("status"));
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
