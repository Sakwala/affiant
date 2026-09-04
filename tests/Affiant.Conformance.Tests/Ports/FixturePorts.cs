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
/// <b>1.0.0-beta.1 never asks it on a decision.</b> <c>ReviewGate.HandleDecisionAsync</c> takes no
/// authorization dependency and consults nothing before transitioning a row, which is the AZ-2
/// defect the oracle records. The port is supplied and registered all the same — a port the driver
/// withheld would turn "the gate never asks" into "the driver never offered", and those are
/// different findings.
/// </remarks>
internal sealed class FixtureAuthorization(AuthorizationSpec spec) : IToolAuthorizationPolicy
{
    /// <summary>Whether the gate ever asked. Nothing in beta.1 does.</summary>
    public bool WasConsulted { get; private set; }

    public Task<bool> AuthorizeAsync(string functionName, string userId, ConversationContext context)
    {
        WasConsulted = true;
        if (spec.Throws)
        {
            throw new InvalidOperationException("The host's authorization port fell over (AZ-6).");
        }

        return Task.FromResult(spec.Allow.Contains("*") || spec.Allow.Contains(userId));
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
/// The fixture's binding (<c>external-ref</c> / <c>computation-ref</c>) has <b>no counterpart</b> in
/// <c>1.0.0-beta.1</c>: <c>ProvenanceTag</c> carries a source, a confidence, an evidence string and
/// a conversation turn, and nothing that points at a record an auditor could re-fetch. The
/// resolver therefore delivers the value and the source, and the binding is dropped — which is what
/// makes every <c>bound: true</c> expectation fail, correctly.
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
            null);
        return Task.FromResult<FieldResolution?>(new FieldResolution(Values.ToClr(spec.Value), tag));
    }
}

/// <summary>
/// One entry of the fixture's approval chain, bound to <see cref="IApprovalPolicy"/> — the whole
/// contract a policy has in <c>1.0.0-beta.1</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the binding cannot carry.</b> A fixture's verdict is
/// <c>{ requirement, ttlMs?, threshold?, reason? }</c>; <see cref="IApprovalPolicy"/> returns a bare
/// <see cref="ReviewRequirement"/><c>?</c> and nothing else. A policy's own deadline, its risk
/// ceiling and its reason have nowhere to go, and <c>declaredInputs</c> / <c>declaresThreshold</c>
/// have no expression at all. That is the GT-4 and GT-5 shape the oracle records: the deadline is
/// stamped from one global default before the chain runs, so a verdict cannot name one.
/// </para>
/// <para>
/// <c>null</c> is "no opinion, ask the next policy" in both documents, so abstention binds exactly.
/// </para>
/// </remarks>
internal sealed class FixturePolicy(PolicySpec spec) : IApprovalPolicy
{
    /// <summary>The fixture's declaration of this policy, for the driver's own reporting.</summary>
    public PolicySpec Spec => spec;

    /// <summary>Whether the chain reached this policy at all.</summary>
    public bool WasEvaluated { get; private set; }

    public Task<ReviewRequirement?> EvaluateAsync(Affidavit affidavit, CancellationToken cancellationToken = default)
    {
        WasEvaluated = true;
        return Task.FromResult(spec.Verdict is null
            ? null
            : (ReviewRequirement?)Enum.Parse<ReviewRequirement>(spec.Verdict.Requirement));
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
