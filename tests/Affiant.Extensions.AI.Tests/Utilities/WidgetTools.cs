namespace Affiant.Extensions.AI.Tests.Utilities;

using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// The shared tool fixture for this package's seam tests. <c>CreateWidget</c> is a write tool
/// returning a <see cref="WriteProposal"/> envelope (so <c>ReviewGateFilter</c> engages);
/// <c>LookUpWidget</c> is a plain read (so the same catalog exercises the non-write path).
/// </summary>
internal sealed class WidgetTools
{
    /// <summary>Records every invocation of <see cref="CreateWidget"/>, in order.</summary>
    public List<string> CreateCalls { get; } = [];

    /// <summary>Records every invocation of <see cref="LookUpWidget"/>, in order.</summary>
    public List<string> LookUpCalls { get; } = [];

    [AffiantWriteTool("WriteCreate", "Widget", typeof(WidgetStrategy))]
    public string CreateWidget(string name)
    {
        CreateCalls.Add(name);

        var affidavit = new Affidavit(
            OperationType: "create",
            EntityType: "Widget",
            EntityId: null,
            Fields: [new AffidavitField(
                "name", name, null, ProvenanceChain.From(ProvenanceTag.FromTool("CreateWidget")))],
            AggregateConfidence: 0.9f,
            Warnings: [],
            RequiresConfirmation: false);

        return new WriteProposal("CreateWidget", DateTimeOffset.UtcNow, affidavit).ToJsonString();
    }

    public string LookUpWidget(string name)
    {
        LookUpCalls.Add(name);
        return $"widget:{name}";
    }
}

/// <summary>Minimal strategy so <c>[AffiantWriteTool]</c>'s inference-strategy slot has a type.</summary>
internal sealed class WidgetStrategy : ITaskInferenceStrategy
{
    public string EntityName => "Widget";
    public IReadOnlyList<TaskInferenceField> Fields => [];
    public double? MinimumConfidenceThreshold => null;
}
