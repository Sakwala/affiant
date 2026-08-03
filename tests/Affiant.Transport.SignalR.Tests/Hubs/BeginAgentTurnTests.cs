namespace Affiant.Transport.SignalR.Tests.Hubs;

using System.Diagnostics;
using Affiant.Transport.SignalR.Hubs;
using Affiant.Transport.SignalR.Tests.Infrastructure;
using Xunit;

/// <summary>
/// Verifies BeginAgentTurn's span contract: operation name, scope name,
/// gen_ai.conversation.id tag, affiant.user.intent truncation at 256 chars,
/// and the null-intent branch where the tag must be omitted.
/// </summary>
public sealed class BeginAgentTurnTests
{
    /// <summary>
    /// Thin subclass that promotes the protected-static BeginAgentTurn to public-static
    /// so tests can call it without going through a live SignalR hub instance.
    /// </summary>
    private sealed class Harness : AffiantHub
    {
        public Harness() : base(new NullChatSessionStore(), new NullStreamingTransport()) { }

        public static Activity? Invoke(string conversationId, string? userIntent = null)
            => BeginAgentTurn(conversationId, userIntent);
    }

    /// <summary>
    /// Registers a listener that ensures the Affiant.Framework source returns non-null
    /// sampled activities. Dispose the listener after assertions.
    /// </summary>
    private static ActivityListener RequireSampling()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Affiant.Framework",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // ── span structure ───────────────────────────────────────────────────────

    [Fact]
    public void Span_OperationName_IsInvokeAgent_WithAffiantFrameworkSource()
    {
        using var listener = RequireSampling();
        using var activity = Harness.Invoke("conv-span-1");

        Assert.NotNull(activity);
        Assert.Equal("invoke_agent", activity.OperationName);
        Assert.Equal("Affiant.Framework", activity.Source.Name);
    }

    // ── tag values ───────────────────────────────────────────────────────────

    [Fact]
    public void Tag_ConversationId_CarriesProvidedValue()
    {
        using var listener = RequireSampling();
        using var activity = Harness.Invoke("conv-abc-123");

        Assert.NotNull(activity);
        Assert.Equal("conv-abc-123", activity.GetTagItem("gen_ai.conversation.id"));
    }

    [Fact]
    public void Tag_UserIntent_ShortText_SetVerbatim()
    {
        using var listener = RequireSampling();
        const string intent = "Search for maintenance records";
        using var activity = Harness.Invoke("conv-short", intent);

        Assert.NotNull(activity);
        Assert.Equal(intent, activity.GetTagItem("affiant.user.intent"));
    }

    // ── truncation ───────────────────────────────────────────────────────────

    [Fact]
    public void Tag_UserIntent_ExceededLimit_TruncatedAt256Chars()
    {
        using var listener = RequireSampling();
        var longIntent = new string('A', 300);
        using var activity = Harness.Invoke("conv-long", longIntent);

        Assert.NotNull(activity);
        var captured = activity.GetTagItem("affiant.user.intent") as string;
        Assert.NotNull(captured);
        Assert.Equal(256, captured.Length);
        Assert.Equal(new string('A', 256), captured);
    }

    // ── null intent ──────────────────────────────────────────────────────────

    [Fact]
    public void Tag_UserIntent_NullIntent_TagNotSet()
    {
        using var listener = RequireSampling();
        using var activity = Harness.Invoke("conv-null", null);

        Assert.NotNull(activity);
        Assert.Null(activity.GetTagItem("affiant.user.intent"));
    }
}
