namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Proves the conversation-scoped context fabric isolates concurrent conversations. Two pipeline
/// executions run in parallel, each in its own service scope with its own conversation identity, and
/// each stamps then reads back a field that carries its own conversation id. With a per-conversation
/// (scoped) fabric resolved from the caller's ambient scope, neither side ever observes the other's
/// value; with a shared singleton fabric the un-namespaced keys collide and the values bleed (and the
/// unsynchronised dictionaries can also throw under the interleaving), so this test fails.
/// </summary>
public class ContextFabricConcurrencyTests
{
    private const int Interleavings = 50;

    [Fact]
    public async Task ConcurrentConversations_ProjectOnlyTheirOwnFieldChains_UnderStress()
    {
        await using var root = BuildProvider();
        var pipeline = root.GetRequiredService<ToolInvocationPipeline>();

        for (var i = 0; i < Interleavings; i++)
        {
            var a = RunConversationAsync(root, pipeline, "conversation-A");
            var b = RunConversationAsync(root, pipeline, "conversation-B");

            var results = await Task.WhenAll(a, b);

            Assert.Equal("conversation-A|Extracted from conversation-A", results[0]);
            Assert.Equal("conversation-B|Extracted from conversation-B", results[1]);
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore();
        services.AddScoped<IToolInvocationFilter, ConversationStampFilter>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<string> RunConversationAsync(
        IServiceProvider root, ToolInvocationPipeline pipeline, string conversationId)
    {
        using var scope = root.CreateScope();
        var request = new ToolInvocationRequest("WriteThing", "ThingPlugin", new Dictionary<string, object?>())
        {
            ConversationId = conversationId,
        };

        var context = await pipeline.RunAsync(
            request,
            filters => filters,
            _ => Task.CompletedTask,
            scope.ServiceProvider);

        return (string)context.Result!;
    }

    /// <summary>
    /// Writes the ambient conversation id into the fabric (entity field + field chain), yields to
    /// force interleaving with the concurrent conversation, then reads both back. The read-back value
    /// is surfaced as the tool result so the test can assert each side saw only its own writes.
    /// </summary>
    private sealed class ConversationStampFilter(IContextFabric fabric) : IToolInvocationFilter
    {
        public async Task OnToolInvocationAsync(
            ToolInvocationContext context,
            Func<ToolInvocationContext, Task> next,
            CancellationToken cancellationToken = default)
        {
            var conversationId = context.ConversationId!;

            fabric.Upsert(new EntityRef(
                EntityType: "Thing",
                EntityId: "thing",
                DisplayName: conversationId,
                Fields: new Dictionary<string, object> { ["owner"] = conversationId }));
            fabric.SetFieldChain("owner", ProvenanceChain.From(ProvenanceTag.FromTool(conversationId)));

            await Task.Yield();
            await next(context);
            await Task.Yield();

            var seenOwner = fabric.GetByKey("thing")?.Fields.GetValueOrDefault("owner") as string;
            var seenEvidence = fabric.GetFieldChain("owner")?.Current.Evidence;
            context.Result = $"{seenOwner}|{seenEvidence}";
        }
    }
}
