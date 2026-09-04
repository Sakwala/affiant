namespace Affiant.Core.Validation;

using System.Text;
using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fails the host at startup when <see cref="ReviewGate"/>'s two host-supplied dependencies —
/// <see cref="IStreamingTransport"/> and <see cref="IDocketStore"/> — were never registered by any
/// package in the application, naming each missing contract and the exact call (and package) that
/// supplies it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (area-8 ruling 6, adopter-cold-start evidence pack, 2026-08-20).</b>
/// <see cref="ReviewGate"/> is registered by <c>AddAffiantCore</c> and resolves
/// <see cref="IStreamingTransport"/>/<see cref="IDocketStore"/> lazily, so a host that forgets
/// <c>Affiant.Transport.SignalR</c> or a Docket backend starts, serves traffic, and holds a normal
/// conversation with no error at all. The gap surfaces only when a tool first produces a
/// <c>WriteProposal</c> and <c>ReviewGateFilter</c> tries to file it — mid-conversation, as a filing
/// failure, at the one moment provenance was supposed to be captured. That is the exact inversion of
/// the loudness rule the repo applies elsewhere: the MAF adapter's <c>HostedToolAudit</c> refuses at
/// wire-up rather than let an uncoverable tool run once, and <c>Affiant.SemanticKernel</c>'s
/// <c>AffiantStartupValidator</c> refuses at startup rather than let an unregistered
/// <c>[KernelFunction]</c> reach the model. This validator gives the review loop the same treatment.
/// </para>
/// <para>
/// <b>Why a startup <see cref="IHostedService"/> and not eager validation inside <c>AddAffiantCore</c>
/// (mechanism choice, recorded per the ruling).</b> The check must run <em>after</em> the whole
/// composition root exists. Every ordering is legal and both reference hosts use different ones —
/// Meridian registers its persistence near the top of <c>Program.cs</c> and calls
/// <c>AddAffiantCore</c> 130 lines later; HR Portal calls <c>AddAffiantCore</c> first and registers
/// Docket/EntityFramework at the very end. Validating inside <c>AddAffiantCore</c> would therefore
/// reject correct wiring roughly half the time. Options validation (<c>ValidateOnStart</c>) was the
/// other candidate and was rejected: it validates an options object's <em>values</em>, has nothing to
/// say about which service contracts are registered, and would pull <c>Microsoft.Extensions.Options</c>
/// plus <c>Microsoft.Extensions.Hosting</c> into <c>Affiant.Core</c> to express a question the DI
/// container already answers directly. A named hosted service asking
/// <see cref="IServiceProviderIsService"/> at <c>StartAsync</c> is the least-magic mechanism that
/// genuinely runs at startup: it is ordinary DI, it reads as what it is in a stack trace, and its
/// only cost is <c>Microsoft.Extensions.Hosting.Abstractions</c> (already in the ASP.NET Core shared
/// framework) on <c>Affiant.Core</c>.
/// </para>
/// <para>
/// <b>Registration presence, not resolution.</b> The check asks
/// <see cref="IServiceProviderIsService.IsService(System.Type)"/> rather than resolving anything:
/// <see cref="IDocketStore"/> is Scoped for both SQL backends, and resolving a Scoped service from
/// the root provider is itself an error under <c>ValidateScopes</c>. Containers that do not provide
/// <see cref="IServiceProviderIsService"/> (some third-party ones) skip the check with a debug log
/// rather than guess.
/// </para>
/// <para>
/// <b>Opt-out.</b> <see cref="AffiantCoreOptions.AcknowledgeMissingReviewWiring"/> downgrades the
/// throw to one warning per missing contract, mirroring
/// <c>AgentFrameworkOptions.AcknowledgeUncoveredTools</c>'s explicit, auditable, never-silent shape.
/// It exists for the host that deliberately runs Affiant's read/inference half with no review loop.
/// </para>
/// </remarks>
public sealed class AffiantWireUpValidator(
    AffiantCoreOptions options,
    ILogger<AffiantWireUpValidator> logger,
    IServiceProviderIsService? isService = null,
    IAffiantToolRegistry? toolRegistry = null,
    IServiceScopeFactory? scopeFactory = null,
    TimeProvider? timeProvider = null) : IHostedService
{
    /// <summary>The one clock (GT-4): whether a deadline can be stamped is asked of the same one.</summary>
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private const string TransportFix =
        "call services.AddAffiantSignalR<THub>() (package Affiant.Transport.SignalR, plus " +
        "app.MapAffiantSignalR<THub>(...) in the request pipeline), or register your own " +
        "IStreamingTransport implementation";

    private static string PreviousValueSourceFix(IReadOnlyList<string> updateTools) =>
        "call services.AddPreviousValueSource<TSource>() with a source that reads the entity's " +
        "stored values from your own system of record — needed because " +
        $"[{string.Join(", ", updateTools)}] declare update operations, and an update-shaped " +
        "Affidavit carries the value each field replaces so a reviewer can see what is changing. " +
        "Register a source, or declare those tools as creates";

    private static string ReviewContextProviderFix(IReadOnlyList<string> writeTools) =>
        "register a host IReviewContextProvider that builds a ReviewContext from the caller's " +
        "identity — needed because " + $"[{string.Join(", ", writeTools)}] " +
        "are declared write-capable, and a proposal the gate cannot route to a reviewer is a write " +
        "nobody reviews";

    private static string DecisionAuthorizationFix(IReadOnlyList<string> writeTools) =>
        "call services.AddDecisionAuthorization<TPolicy>() with a policy that answers whether a " +
        "principal may decide a given Docket entry — needed because " +
        $"[{string.Join(", ", writeTools)}] are declared write-capable, and who may approve a " +
        "write is the one question about the review loop the framework cannot answer for you. " +
        "Without one the gate falls back to DenyAllDecisionAuthorization and every decision, " +
        "execution report and resubmission is refused: safe, and unusable";

    private static string ReviewGateFix(IReadOnlyList<string> writeTools) =>
        "call services.AddAffiantCore(...) in this application's composition root — needed because " +
        $"[{string.Join(", ", writeTools)}] are declared write-capable and there is nothing " +
        "registered to file their proposals with";

    private const string DocketStoreFix =
        "call services.AddAffiantEntityFramework(ef => ef.UseSqlite(...) | ef.UsePostgres(...)) " +
        "(package Affiant.EntityFramework) for a durable review queue, or " +
        "services.AddAffiantDocket(d => d.UseInMemory()) (package Affiant.Docket) for a " +
        "process-local one";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateDeadline();

        if (isService is null)
        {
            logger.LogDebug(
                "Affiant wire-up validation skipped: this DI container does not provide " +
                "IServiceProviderIsService, so registered contracts cannot be enumerated without " +
                "resolving them.");
            return Task.CompletedTask;
        }

        var missing = new List<(string Contract, string Fix)>();

        if (!isService.IsService(typeof(IStreamingTransport)))
            missing.Add((typeof(IStreamingTransport).FullName!, TransportFix));

        if (!isService.IsService(typeof(IDocketStore)))
            missing.Add((typeof(IDocketStore).FullName!, DocketStoreFix));

        // An update-shaped Affidavit swears to what each field replaces, and only the host's own
        // system of record knows that — so a host whose write tools declare update operations and
        // registers no IPreviousValueSource cannot produce a lawful update Affidavit at all. The
        // check is conditional on purpose: a create-only host is unaffected and nothing about its
        // wiring changes.
        var updateTools = toolRegistry?.All
            .Where(d => Operation.IsUpdateShaped(d.Operation.Kind))
            .Select(Describe)
            .ToArray() ?? [];

        if (updateTools.Length > 0 && !isService.IsService(typeof(IPreviousValueSource)))
            missing.Add((typeof(IPreviousValueSource).FullName!, PreviousValueSourceFix(updateTools)));

        // CV-1: a tool this application declares write-capable must have somewhere for its proposal
        // to go and someone for the review to be routed to, or the gate is a decoration. Two of the
        // three ways ReviewGateFilter could previously pass a write through unreviewed are visible
        // from here — no IReviewContextProvider and no ReviewGate — and refusing them at startup is
        // strictly better than refusing the first write of the first conversation. The third (a
        // provider that returns no context for this particular call) only a live request can know,
        // and the filter refuses it there.
        var writeTools = toolRegistry?.All
            .Where(d => d.Operation.Kind != Operation.ReadQuery.Kind)
            .Select(Describe)
            .ToArray() ?? [];

        var gateMissing = new List<(string Contract, string Fix)>();
        if (writeTools.Length > 0)
        {
            if (!isService.IsService(typeof(IReviewContextProvider)))
                gateMissing.Add((typeof(IReviewContextProvider).FullName!, ReviewContextProviderFix(writeTools)));

            if (!isService.IsService(typeof(ReviewGate)))
                gateMissing.Add((typeof(ReviewGate).FullName!, ReviewGateFix(writeTools)));

            // AZ-2: who may decide is host policy, and the framework will not guess it. The gate
            // fails closed without one — DenyAllDecisionAuthorization refuses everything — so this
            // check is not what makes the application safe; it is what stops a host from shipping a
            // review loop in which no decision can ever be accepted, and from discovering that the
            // first time a reviewer presses approve.
            if (!isService.IsService(typeof(IDecisionAuthorizationPolicy)))
            {
                gateMissing.Add((
                    typeof(IDecisionAuthorizationPolicy).FullName!,
                    DecisionAuthorizationFix(writeTools)));
            }
        }

        // CV-1: a policy that declares a risk threshold with no scorer wired is a wire-up refusal,
        // not a silent non-fire. The chain is resolved in a throwaway scope and asked; nothing is
        // evaluated and nothing is approved. A container that cannot build the chain at startup is
        // itself the fault, and the exception says which policy could not be constructed.
        foreach (var fault in PolicyConfigurationFaults())
            gateMissing.Add((fault.PolicyId, fault.Reason));

        if (missing.Count == 0 && gateMissing.Count == 0) return Task.CompletedTask;

        // "No option turns the gate off for a tool it covers" (CV-1). The acknowledgment exists for
        // the host that deliberately runs Affiant's read/inference half with no review loop — and a
        // host that has declared a write-capable tool is, by its own declaration, not that host. So
        // it downgrades the review-wiring contracts and never these two.
        if (options.AcknowledgeMissingReviewWiring && gateMissing.Count == 0)
        {
            foreach (var (contract, fix) in missing)
            {
                logger.LogWarning(
                    "Affiant: no {Contract} is registered, so ReviewGate cannot file write proposals " +
                    "for review — every write this application proposes will fail at filing time. " +
                    "Acknowledged via AffiantCoreOptions.AcknowledgeMissingReviewWiring. To fix: {Fix}.",
                    contract, fix);
            }

            return Task.CompletedTask;
        }

        var message = new StringBuilder();
        message.AppendLine(
            "Affiant.Core: AddAffiantCore() registered the write-review path — ReviewGate, the state " +
            "machine every write proposal is filed through, and the Affidavit projection that builds " +
            "what a reviewer sees — but the following contracts it needs were not registered by any " +
            "package in this application:");
        foreach (var (contract, fix) in missing.Concat(gateMissing))
            message.AppendLine($"- {contract} — {fix}.");
        message.AppendLine();
        if (gateMissing.Count > 0)
        {
            message.AppendLine(
                "AffiantCoreOptions.AcknowledgeMissingReviewWiring does not apply to the entries " +
                "above that name a write-capable tool: it exists for a host that deliberately runs " +
                "the read and inference half with no review loop, and this host has declared tools " +
                "that propose writes. There is no option that turns the gate off for a tool it " +
                "covers — declare those tools as reads with services.AddAffiantReadTool(...) if they " +
                "genuinely do not write.");
            message.AppendLine();
        }
        message.AppendLine(
            "Without them the application starts and converses normally, and the gap surfaces only " +
            "when a tool first produces a WriteProposal — mid-conversation, at the one moment " +
            "provenance was supposed to be captured, either as a filing failure or as an update " +
            "Affidavit that swears to no previous values at all. Failing here instead is deliberate " +
            "(area-8 ruling 6).");
        message.AppendLine(
            "If this host deliberately runs without a review loop, set " +
            "AffiantCoreOptions.AcknowledgeMissingReviewWiring = true in AddAffiantCore(options => ...) " +
            "to downgrade this to a startup warning.");

        throw new AffiantStartupException(message.ToString());
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Every registered approval policy that says it cannot run as it is wired (CV-1).
    /// </summary>
    /// <remarks>
    /// The chain is resolved in a throwaway scope and each policy is asked
    /// <see cref="IApprovalPolicy.ConfigurationFault"/>. Nothing is evaluated, no Affidavit is seen
    /// and nothing is approved: the question is about the wiring, not about a write. A host whose
    /// container cannot build a policy at all fails here too, which is the same class of fault one
    /// step earlier.
    /// </remarks>
    private IEnumerable<(string PolicyId, string Reason)> PolicyConfigurationFaults()
    {
        if (scopeFactory is null) yield break;

        using var scope = scopeFactory.CreateScope();
        foreach (var policy in scope.ServiceProvider.GetServices<IApprovalPolicy>())
        {
            if (policy.ConfigurationFault is { Length: > 0 } fault)
                yield return (policy.PolicyId, fault);
        }
    }

    private static string Describe(AffiantToolDescriptor d) =>
        d.PluginName is null ? d.FunctionName : $"{d.PluginName}.{d.FunctionName}";

    /// <summary>
    /// Refuses a review deadline no entry could survive. A time-to-live that is not at least one
    /// millisecond, or that is large enough to overflow the stamp, is a wire-up error — never an
    /// entry born expired, and never an entry whose deadline is silently clamped.
    ///
    /// <para>
    /// Checked here, before the container questions, because it needs nothing from the container and
    /// because the failure it prevents is the quietest one in the gate: with a zero deadline every
    /// entry is filed already past <c>ExpiresAt</c>, the sweep expires it on the next tick, and every
    /// review "times out" with no error anywhere. There is no acknowledgment switch for it, unlike
    /// <see cref="AffiantCoreOptions.AcknowledgeMissingReviewWiring"/>: a host can knowingly run
    /// without a review loop, but no host means a deadline of zero.
    /// </para>
    /// </summary>
    private void ValidateDeadline()
    {
        var ttl = options.DefaultDocketTtl;
        var overflows = ttl > DateTimeOffset.MaxValue - _time.GetUtcNow();
        if (ttl >= TimeSpan.FromMilliseconds(1) && !overflows) return;

        var reason = overflows
            ? "the deadline is too far in the future to stamp on an entry"
            : "a deadline must be at least one millisecond";

        // TL-1 `policy.invalid` (GT-4, CV-1). Emitted before the throw so a host whose startup
        // failure is only visible in a collector still sees which option broke and why.
        AffiantTelemetry.RecordPolicyInvalid(
            typeof(AffiantCoreOptions).FullName!,
            option: $"{nameof(AffiantCoreOptions)}.{nameof(AffiantCoreOptions.DefaultDocketTtl)}",
            reason: reason);

        throw new AffiantStartupException(
            $"Affiant.Core: AffiantCoreOptions.DefaultDocketTtl is {ttl}, which is not a usable " +
            $"review deadline — {reason}. Every DocketEntry's ExpiresAt and ReviewGate's own await " +
            "window are stamped from this value, so a review filed under it could never be decided. " +
            "Set it in AddAffiantCore(options => options.DefaultDocketTtl = ...) to the window a " +
            "reviewer genuinely has (the default is 30 minutes).");
    }
}
