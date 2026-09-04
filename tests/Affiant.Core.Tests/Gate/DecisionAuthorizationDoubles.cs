namespace Affiant.Core.Tests.Gate;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// The host authorization port a test that is <em>not</em> about authorization wires: every
/// principal may act on every entry, so the test exercises the behaviour it names and nothing else.
/// </summary>
/// <remarks>
/// Deliberately not shipped in any package. The framework's own default is
/// <c>DenyAllDecisionAuthorization</c>, and a permissive one that adopters could reach for by
/// accident is exactly the fail-open this seam exists to close.
/// </remarks>
internal sealed class AllowAllDecisionAuthorization : IDecisionAuthorizationPolicy
{
    public int Calls { get; private set; }

    /// <summary>The last principal this port was asked about, or <c>null</c> if it never was.</summary>
    public Principal? LastPrincipal { get; private set; }

    public Task<bool> MayDecideAsync(
        Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastPrincipal = principal;
        return Task.FromResult(true);
    }
}

/// <summary>A host port that says no. False refuses, and the gate reports it as unauthorized.</summary>
internal sealed class DeclineAllDecisionAuthorization : IDecisionAuthorizationPolicy
{
    public Task<bool> MayDecideAsync(
        Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// A host port that falls over. A callback that threw has not said yes, so the gate reads it as a
/// refusal — never as an approval, and never as an exception the caller has to catch.
/// </summary>
internal sealed class ThrowingDecisionAuthorization : IDecisionAuthorizationPolicy
{
    public Task<bool> MayDecideAsync(
        Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("the host's membership directory is unreachable");
}
