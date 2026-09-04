namespace Affiant.Core.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// The authorization port a host gets when it registers none: nobody may decide anything.
/// </summary>
/// <remarks>
/// <para>
/// A framework that refuses every decision is obviously broken, and that is the point — it is
/// broken <em>loudly and safely</em>, in the direction that cannot approve a write nobody was
/// entitled to approve. The alternative default is "admit everyone", which is the failure this
/// whole seam exists to close: a fail-open on unresolved or unchecked identity is an authorization
/// bypass the moment a real deployment's identity resolution can fail, which it eventually will.
/// </para>
/// <para>
/// A host is not expected to run on this. <c>AffiantWireUpValidator</c> refuses at startup when the
/// application declares a write-capable tool and no <see cref="IDecisionAuthorizationPolicy"/> is
/// registered, so the deny-all only ever governs the window before that check runs and any path
/// that reaches the gate without going through it.
/// </para>
/// </remarks>
public sealed class DenyAllDecisionAuthorization : IDecisionAuthorizationPolicy
{
    /// <summary>The single instance the gate falls back to when nothing is registered.</summary>
    public static DenyAllDecisionAuthorization Instance { get; } = new();

    /// <inheritdoc />
    public Task<bool> MayDecideAsync(
        Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
