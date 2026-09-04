using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Conformance.Tests.Model;

namespace Affiant.Conformance.Tests.Ports;

/// <summary>
/// The authorization port of <c>RUNNER.md</c> §7, built from <c>given.gate.authorization</c>:
/// admits a principal iff its id is in <c>allow</c>, or <c>allow</c> holds <c>"*"</c>; with
/// <c>throws</c>, it falls over instead of answering.
/// </summary>
/// <remarks>
/// It binds both authorization seams — <see cref="IToolAuthorizationPolicy"/> at the tool and
/// <see cref="IDecisionAuthorizationPolicy"/> at the decision — because <c>given.gate.authorization</c>
/// is "who may decide" and a port the driver withheld would turn "the gate never asks" into "the
/// driver never offered", which are different findings.
/// </remarks>
internal sealed class FixtureAuthorization(AuthorizationSpec spec)
    : IToolAuthorizationPolicy, IDecisionAuthorizationPolicy
{
    /// <summary>Whether the gate asked, on either seam.</summary>
    public bool WasConsulted { get; private set; }

    /// <summary>The fixture's clock, so an unset one never reads the wall clock.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UnixEpoch;

    public Task<bool> AuthorizeAsync(string functionName, string userId, ConversationContext context)
    {
        WasConsulted = true;
        if (spec.Throws)
        {
            throw new InvalidOperationException("The host's authorization port fell over (AZ-6).");
        }

        return Task.FromResult(spec.Allow.Contains("*") || spec.Allow.Contains(userId));
    }

    /// <inheritdoc />
    public Task<bool> MayDecideAsync(Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        WasConsulted = true;
        if (spec.Throws)
        {
            throw new InvalidOperationException("The host's authorization port fell over (AZ-6).");
        }

        return Task.FromResult(spec.Allow.Contains("*") || spec.Allow.Contains(principal.PrincipalId));
    }
}

/// <summary>
/// The inference port of <c>RUNNER.md</c> §7, built from <c>given.gate.inference</c>: it reports
/// exactly the scripted fields, for every turn, unchanged — no invention, no filtering, no
/// re-scoring. Absent or <c>null</c>: it reports nothing.
/// </summary>
/// <remarks>
/// Bound to <see cref="IInferenceCompletionPort"/>, the seam <c>TaskInferenceRunner</c> calls, so
/// the framework's own merge (<c>TaskInferenceStep</c>) runs over the scripted answer rather than
/// the driver writing tags into the fabric itself. The shape is the one that step reads: one
/// property per field name, each <c>{ "value": …, "confidence": … }</c>.
/// </remarks>
internal sealed class ScriptedInference(IReadOnlyDictionary<string, InferredFieldSpec> scripted) : IInferenceCompletionPort
{
    public Task<JsonElement> CompleteStructuredAsync(InferenceCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var document = new JsonObject();
        foreach (var (name, field) in scripted)
        {
            document[name] = new JsonObject
            {
                ["value"] = field.Value?.DeepClone(),
                ["confidence"] = JsonValue.Create(field.Confidence),
            };
        }

        return Task.FromResult(JsonSerializer.Deserialize<JsonElement>(document.ToJsonString()));
    }
}

/// <summary>
/// One deterministic resolver from <c>given.gate.interceptors</c> (PV-2, GT-1 step 2), bound to
/// <see cref="IFieldResolver"/> — the seam the projection consults ahead of the fabric's own chain.
/// </summary>
/// <remarks>
/// The fixture's binding (<c>external-ref</c> / <c>computation-ref</c>) travels on the tag: the
/// resolver delivers the value, the source, the confidence and the binding, so what a fixture's
/// <c>bound: true</c> measures is the framework's handling of it and not the driver's.
/// </remarks>
internal sealed class FixtureFieldResolver(string fieldName, InterceptedFieldSpec spec) : IFieldResolver
{
    public string FieldName => fieldName;

    public Task<FieldResolution?> ResolveAsync(FieldResolutionContext context, CancellationToken cancellationToken)
    {
        var tag = new ProvenanceTag(
            Enum.Parse<ProvenanceSource>(spec.Source),
            (float)spec.Confidence,
            spec.Evidence,
            ConversationTurn: null,
            Binding: Bindings.ToFramework(spec.Binding));
        return Task.FromResult<FieldResolution?>(new FieldResolution(Values.ToClr(spec.Value), tag));
    }
}

/// <summary>
/// One entry of the fixture's approval chain, bound to <see cref="IApprovalPolicy"/> — the whole
/// contract a policy has in <c>1.0.0-beta.1</c>.
/// </summary>
/// <remarks>
/// <para>
/// A fixture's verdict is <c>{ requirement, ttlMs?, threshold?, reason? }</c> and an
/// <see cref="ApprovalVerdict"/> carries every part of it: the requirement, the policy's own review
/// window (GT-4) and its reason. The policy's <c>id</c>, <c>version</c> and <c>declaredInputs</c>
/// (PV-4) bind to <see cref="IApprovalPolicy.PolicyId"/>,
/// <see cref="IApprovalPolicy.PolicyVersion"/> and <see cref="IApprovalPolicy.DeclaredInputs"/>.
/// </para>
/// <para>
/// A verdict's <c>threshold</c> and the fixture's <c>declaresThreshold</c> are the risk ceiling
/// (GT-5, CV-1). This policy is a plain <see cref="IApprovalPolicy"/> rather than a
/// <c>StandingOrderBase</c>, so the driver applies the comparison the framework would: with a
/// scorer wired, a score above the ceiling degrades the verdict to reviewer confirmation, keeping
/// the window; with no scorer wired the policy declares the threshold and lets the wire-up
/// validator refuse the host, which is where CV-1 puts that fault.
/// </para>
/// <para>
/// <c>null</c> is "no opinion, ask the next policy" in both documents, so abstention binds exactly.
/// </para>
/// </remarks>
internal sealed class FixturePolicy(PolicySpec spec, double? riskScore) : IApprovalPolicy
{
    /// <summary>The fixture's declaration of this policy, for the driver's own reporting.</summary>
    public PolicySpec Spec => spec;

    /// <summary>Whether the chain reached this policy at all.</summary>
    public bool WasEvaluated { get; private set; }

    /// <inheritdoc />
    public string PolicyId => spec.Id;

    /// <inheritdoc />
    public string? PolicyVersion => spec.Version;

    /// <inheritdoc />
    public IReadOnlyCollection<ProvenanceSource> DeclaredInputs { get; } =
        spec.DeclaredInputs.Select(Enum.Parse<ProvenanceSource>).ToArray();

    /// <inheritdoc />
    public TimeSpan? DefaultTimeToLive =>
        spec.DefaultTtlMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;

    /// <summary>
    /// Whether any verdict this policy can return names a risk ceiling — the fact CV-1's wire-up
    /// check reads, independently of whether this evaluation reaches the comparison.
    /// </summary>
    public bool DeclaresThreshold => spec.DeclaresThreshold || spec.Verdict?.Threshold is not null;

    public Task<ApprovalVerdict?> EvaluateAsync(
        Affidavit affidavit,
        ConversationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        WasEvaluated = true;
        if (spec.Verdict is null)
            return Task.FromResult<ApprovalVerdict?>(null);

        var verdict = new ApprovalVerdict(
            Enum.Parse<ReviewRequirement>(spec.Verdict.Requirement),
            TimeToLive: spec.Verdict.TtlMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            Reason: spec.Verdict.Reason,
            PolicyId: spec.Id,
            PolicyVersion: spec.Version);

        // The risk comparison, where this fixture declares one and a scorer is wired. The framework
        // owns the comparison; the host owns the number (GT-5).
        if (verdict.Requirement == ReviewRequirement.StandingOrder
            && spec.Verdict.Threshold is { } ceiling
            && riskScore is { } score
            && score > ceiling)
        {
            verdict = verdict.DegradeToReviewer(
                StandingOrderBlockedReasons.RiskAboveThreshold,
                $"risk {score} is above the ceiling {ceiling}");
        }

        return Task.FromResult<ApprovalVerdict?>(verdict);
    }
}

/// <summary>
/// The executor GT-6 makes a tripwire: the gate stands in front of writes and must never perform
/// one, so this fails if it is ever called.
/// </summary>
/// <remarks>
/// A driver that supplied a harmless no-op would turn every Sequence A fixture into a fixture that
/// cannot detect the bug it is there for.
/// </remarks>
internal sealed class TripwireWriteExecutor : IWriteExecutor
{
    /// <summary>True once the gate has performed a write it must never have performed.</summary>
    public bool WasCalled { get; private set; }

    public Task<string?> ExecuteAsync(Affidavit affidavit, IReadOnlyDictionary<string, object?>? amendments, CancellationToken ct)
    {
        WasCalled = true;
        throw new InvalidOperationException(
            "GT-6: the gate performed the write itself. The executor is a tripwire and must never be reached.");
    }
}
