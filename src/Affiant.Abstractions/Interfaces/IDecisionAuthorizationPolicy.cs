namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// The host's answer to one question: <em>may this principal act on this Docket entry?</em>
/// </summary>
/// <remarks>
/// <para>
/// <b>What the framework does and what this port does.</b> The framework refuses an unresolved
/// principal before it reads the Docket, and it compares the row's tenant with the caller's itself
/// rather than trusting a store's scope. Neither of those is delegated here, because a check
/// hand-rolled per host tends to check the acting user and not the tenant, and to fall open when
/// identity resolution fails. What is left is the question only the host can answer — whether
/// <em>this</em> person, in a tenant that already matched, is one of the people entitled to decide
/// <em>this</em> row: the reviewer it was routed to, anyone in a role, anyone at all in a
/// single-user deployment. The framework has no opinion about that and will not invent one.
/// </para>
/// <para>
/// <b>It fails closed twice over.</b> Returning <c>false</c> refuses the act with
/// <c>decision-unauthorized</c>, and so does throwing: an authorization callback that fell over has
/// not said yes, and the gate never reads a fault as an approval. A host that registers no
/// implementation at all gets <c>DenyAllDecisionAuthorization</c>, which refuses everything — and,
/// because a framework that silently refuses every decision is its own kind of failure, the
/// wire-up validator refuses at startup when this application declares a write-capable tool and no
/// policy is registered. The default exists so the runtime is never fail-open in the window before
/// startup validation runs, not as a configuration anybody should ship.
/// </para>
/// <para>
/// <b>This is not <see cref="IApprovalPolicy"/>.</b> An approval policy decides <em>how</em> a
/// proposed write must be approved — a standing order, a person's confirmation, a review window.
/// This port decides <em>who</em> may give that approval once it has been asked for. Identity is
/// supplied to an approval policy so it can <em>bind</em> (a member-bound standing order);
/// authorizing the actor is this port's job and the framework's enforcement, never a policy's.
/// </para>
/// <para>
/// <b>It guards three surfaces, not one.</b> The decision itself, the host's execution report and a
/// resubmission all run the same checks in the same order, so there is no entry point on the gate
/// that moves a row without asking this question. A machine caller is admitted here and refused as
/// a <em>decider</em>: reporting an outcome is a statement of fact about work the host performed,
/// which a machine is the right party to make, while a decision is an act of authority a machine
/// may never make in a person's name.
/// </para>
/// </remarks>
public interface IDecisionAuthorizationPolicy
{
    /// <summary>
    /// May <paramref name="principal"/> decide, report on, or resubmit <paramref name="entry"/>?
    /// </summary>
    /// <param name="principal">
    /// Who is acting. Never <c>null</c>: the gate refuses an unresolved principal before it reads
    /// the Docket, so this port is never asked to rule on "nobody".
    /// </param>
    /// <param name="entry">
    /// The row, already read and already confirmed to be inside the caller's tenant. An
    /// implementation does not need to re-check the tenant and should not treat this parameter as
    /// evidence that anything else was checked.
    /// </param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>
    /// <c>true</c> to admit the act. Anything else — <c>false</c>, or a throw — refuses it.
    /// </returns>
    Task<bool> MayDecideAsync(
        Principal principal, DocketEntry entry, CancellationToken cancellationToken = default);
}
