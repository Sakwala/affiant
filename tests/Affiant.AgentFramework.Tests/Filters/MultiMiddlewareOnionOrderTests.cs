namespace Affiant.AgentFramework.Tests.Filters;

using Affiant.AgentFramework.Tests.Utilities;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

/// <summary>
/// Pins the actual chaining order MAF uses when two <c>.Use(...)</c> function-invocation
/// middlewares are registered on the same <see cref="AIAgentBuilder"/>. Microsoft's docs describe
/// <c>AIAgentBuilder.Use</c>'s general agent-decorator ordering ("the first factory added is the
/// outermost") but do not separately document the ordering of nested <c>AIFunction</c> wrapping
/// that function-invocation middleware performs via <c>ChatOptions.Tools</c> mutation — proposal
/// §6 calls this out explicitly as unspecified and requires an executable pin.
///
/// Empirically (this test): for function-invocation middleware specifically, the order observed
/// around the actual tool call is the REVERSE of registration order — the middleware registered
/// LAST becomes the outermost wrapper around the tool invocation, and the FIRST-registered
/// middleware sits closest to the tool. This is the opposite of AIAgentBuilder's own
/// agent-decorator convention, because function-invocation middleware nests by wrapping the
/// AIFunction objects placed in ChatOptions.Tools once per RunAsync layer, and the outer agent
/// layer's ConfigureOptions callback runs — and therefore wraps — before the inner layer's.
/// </summary>
public class MultiMiddlewareOnionOrderTests
{
    [Fact]
    public async Task SecondRegisteredMiddleware_IsOutermost_AroundToolInvocation()
    {
        var order = new List<string>();

        string DoThing(string x)
        {
            order.Add("tool-ran:" + x);
            return "done:" + x;
        }

        var tool = AIFunctionFactory.Create((Func<string, string>)DoThing, name: "DoThing");
        var chatClient = new ScriptedChatClient("DoThing", new Dictionary<string, object?> { ["x"] = "arg" });
        var agent = new ChatClientAgent(chatClient, instructions: "test agent", tools: [tool]);

        var wrapped = agent.AsBuilder()
            .Use(async (_, context, next, ct) =>
            {
                order.Add("mw1-before");
                var result = await next(context, ct);
                order.Add("mw1-after");
                return result;
            })
            .Use(async (_, context, next, ct) =>
            {
                order.Add("mw2-before");
                var result = await next(context, ct);
                order.Add("mw2-after");
                return result;
            })
            .Build();

        var session = await wrapped.CreateSessionAsync();
        await wrapped.RunAsync("hello", session);

        Assert.Equal(
            ["mw2-before", "mw1-before", "tool-ran:arg", "mw1-after", "mw2-after"],
            order);
    }
}
