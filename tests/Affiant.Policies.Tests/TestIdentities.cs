using Affiant.Abstractions.Models;

/// <summary>
/// The conversation identity a test that is not about binding passes to an approval policy.
/// </summary>
/// <remarks>
/// Identity reaches a policy so it can <em>bind</em> — to a member, a tenant, a channel — never so
/// it can authorize the actor, which the framework enforces itself before any transition. A test
/// about a policy's requirement or its review window therefore has nothing to say about identity,
/// and says it once, here, rather than inventing a different placeholder in every file.
/// </remarks>
internal static class TestIdentities
{
    /// <summary>Somebody, somewhere, on some channel. Deliberately unremarkable.</summary>
    public static ConversationIdentity Anyone { get; } = new(
        SessionId: "session-test",
        UserId: "user-123",
        StartedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        HostAppName: "tests",
        TenantId: "tenant-default",
        Channel: "test");
}
