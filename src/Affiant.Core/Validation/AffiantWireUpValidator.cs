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
    IAffiantToolRegistry? toolRegistry = null) : IHostedService
{
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
            .Select(d => d.PluginName is null ? d.FunctionName : $"{d.PluginName}.{d.FunctionName}")
            .ToArray() ?? [];

        if (updateTools.Length > 0 && !isService.IsService(typeof(IPreviousValueSource)))
            missing.Add((typeof(IPreviousValueSource).FullName!, PreviousValueSourceFix(updateTools)));

        if (missing.Count == 0) return Task.CompletedTask;

        if (options.AcknowledgeMissingReviewWiring)
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
        foreach (var (contract, fix) in missing)
            message.AppendLine($"- {contract} — {fix}.");
        message.AppendLine();
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
        var overflows = ttl > DateTimeOffset.MaxValue - DateTimeOffset.UtcNow;
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
