namespace Affiant.Core.Tests.Filters;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The completion-stage filter is a shipped caller of the merge step — registered unconditionally by
/// <c>AddAffiantCore</c> and position 6 of both bridges' pipelines — and it holds the same
/// conversation history the pre-tool runner does. So it grades the same way: presence is established
/// from the turn, and the port's own claim is a hint (PV-3, design record A10).
/// </summary>
public class TaskInferenceMergeFilterPresenceTests
{
    private sealed class OneFieldStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";

        public IReadOnlyList<TaskInferenceField> Fields { get; } =
            [new("Priority", "string", "How urgent")];

        public double? MinimumConfidenceThreshold => null;
    }

    private static (ContextFabric Fabric, TaskInferenceMergeFilter Filter, IServiceProvider Services) Build()
    {
        var fabric = new ContextFabric();
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor(
            "CreateThing", "ThingPlugin", new Operation("WriteCreate"), "Thing", typeof(OneFieldStrategy)));

        var services = new ServiceCollection()
            .AddSingleton<OneFieldStrategy>()
            .BuildServiceProvider();

        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var filter = new TaskInferenceMergeFilter(
            step, registry, NullLogger<TaskInferenceMergeFilter>.Instance);

        return (fabric, filter, services);
    }

    private static ToolInvocationContext ContextWith(
        IServiceProvider services, string result, params AffiantChatMessage[] history) =>
        new()
        {
            FunctionName = "CreateThing",
            PluginName = "ThingPlugin",
            Arguments = new Dictionary<string, object?>(),
            Services = services,
            History = history,
            Result = result,
        };

    /// <summary>
    /// The defect A10 names: this filter passed no turn, so a value the person typed was still sworn
    /// "AI suggested" on the one shipped path that does not go through the runner. It now grades from
    /// the turn the bridge put on the context.
    /// </summary>
    [Fact]
    public async Task ASilentPort_AndAValueInTheLastUserTurn_IsConversation()
    {
        var (fabric, filter, services) = Build();
        var context = ContextWith(
            services,
            """{ "Priority": { "value": "Critical", "confidence": 0.9 } }""",
            new AffiantChatMessage("user", "Priority Critical, please"));

        await filter.OnToolInvocationAsync(context, _ => Task.CompletedTask);

        var tag = fabric.GetFieldChain("Priority")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        var span = Assert.IsType<ProvenanceBinding.UtteranceSpan>(tag.Binding).Ref;
        Assert.Equal(9, span.Offset);
        Assert.Equal("Critical", "Priority Critical, please".Substring(span.Offset, span.Length));
    }

    /// <summary>
    /// The other half of the same rule at this seam: a port claiming <c>literal</c> for a value the
    /// turn does not contain no longer buys a <c>Conversation</c> grade here either.
    /// </summary>
    [Fact]
    public async Task APortClaimingLiteral_ForAValueThatIsNotInTheTurn_IsInferred()
    {
        var (fabric, filter, services) = Build();
        var context = ContextWith(
            services,
            """
            {
              "Priority": {
                "value": "Critical",
                "confidence": 0.9,
                "presence": "literal",
                "utteranceSpan": { "start": 0, "end": 8 }
              }
            }
            """,
            new AffiantChatMessage("user", "make this one urgent"));

        await filter.OnToolInvocationAsync(context, _ => Task.CompletedTask);

        var tag = fabric.GetFieldChain("Priority")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    /// <summary>
    /// A10: a user message with null content is an empty utterance, so the finder stays in charge
    /// here too and the port's claim does not stand in for it.
    /// </summary>
    [Fact]
    public async Task AUserTurnWithNullContent_IsAnEmptyUtterance()
    {
        var (fabric, filter, services) = Build();
        var context = ContextWith(
            services,
            """{ "Priority": { "value": "Critical", "confidence": 0.9, "presence": "literal" } }""",
            new AffiantChatMessage("user", null!));

        await filter.OnToolInvocationAsync(context, _ => Task.CompletedTask);

        Assert.Equal(ProvenanceSource.Inferred, fabric.GetFieldChain("Priority")!.Current.Source);
    }

    /// <summary>
    /// D4 at this seam: a history with no turn of a person's in it leaves the step nothing to check
    /// against, so the port's own report stands — the one state the no-turn path is for.
    /// </summary>
    [Fact]
    public async Task AHistoryWithNoUserMessage_LeavesThePortsReportStanding()
    {
        var (fabric, filter, services) = Build();
        var context = ContextWith(
            services,
            """{ "Priority": { "value": "Critical", "confidence": 0.9, "presence": "literal" } }""",
            new AffiantChatMessage("system", "You are a helpful assistant."));

        await filter.OnToolInvocationAsync(context, _ => Task.CompletedTask);

        Assert.Equal(ProvenanceSource.Conversation, fabric.GetFieldChain("Priority")!.Current.Source);
    }
}
