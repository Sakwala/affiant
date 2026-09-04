using System.Text.Json.Nodes;

namespace Affiant.Conformance.Tests.Model;

/// <summary>
/// One declarative conformance fixture, as <c>protocol/RUNNER.md</c> defines it: a wiring
/// (<see cref="Given"/>), a sequence of acts, and what must then be true (<see cref="Expect"/>).
/// </summary>
/// <remarks>
/// <see cref="Expect"/> stays a <see cref="JsonNode"/> on purpose. Every matcher in the format is
/// partial — a key the fixture does not state is not checked — so the expectation is compared
/// against a neutral JSON projection of what the framework did, and the dotted path a mismatch is
/// reported at falls out of that walk instead of being hand-written at each clause.
/// </remarks>
internal sealed record Fixture(
    string Id,
    IReadOnlyList<string> Rules,
    string Title,
    GivenSpec Given,
    JsonObject Expect,
    string SourcePath);

/// <summary>The wiring, the acts that set the scene, and the act under test.</summary>
internal sealed record GivenSpec(
    DateTimeOffset Clock,
    string Store,
    GateSpec Gate,
    CtxSpec Ctx,
    IReadOnlyList<StepSpec> Prior,
    StepSpec Step);

/// <summary>Everything the gate is built from (<c>RUNNER.md</c> §2.1).</summary>
internal sealed record GateSpec(
    int DefaultTtlMs,
    AuthorizationSpec Authorization,
    IReadOnlyList<PolicySpec> Policies,
    double? RiskScorer,
    bool RiskScorerStated,
    IReadOnlyList<InterceptorSpec> Interceptors,
    IReadOnlyDictionary<string, InferredFieldSpec> Inference,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode?>> Entities,
    bool EntitiesStated,
    IReadOnlyList<UncoveredSpec> Uncovered,
    bool Sessions);

/// <summary>Who may decide (AZ-2), and whether the port falls over instead of answering (AZ-6).</summary>
internal sealed record AuthorizationSpec(IReadOnlyList<string> Allow, bool Throws);

/// <summary>One entry of the approval chain, in order (AZ-4).</summary>
internal sealed record PolicySpec(
    string Id,
    string Version,
    IReadOnlyList<string> DeclaredInputs,
    bool DeclaresThreshold,
    int? DefaultTtlMs,
    VerdictSpec? Verdict);

/// <summary>What a policy returns, or <c>null</c> for "no opinion".</summary>
internal sealed record VerdictSpec(string Requirement, int? TtlMs, double? Threshold, string? Reason);

/// <summary>A deterministic resolver (PV-2, GT-1 step 2).</summary>
internal sealed record InterceptorSpec(string Name, IReadOnlyDictionary<string, InterceptedFieldSpec> Fields);

/// <summary>One field an interceptor resolves, with the tag it puts on it.</summary>
internal sealed record InterceptedFieldSpec(
    JsonNode? Value,
    string Source,
    double Confidence,
    BindingSpec? Binding,
    string? Evidence);

/// <summary>The two binding kinds a machine may mint (PV-3).</summary>
internal sealed record BindingSpec(string Kind, JsonObject Ref);

/// <summary>What the host's inference reports for one field (GT-1 step 3). Scripted, never computed.</summary>
internal sealed record InferredFieldSpec(JsonNode? Value, double Confidence, string Presence, JsonObject? UtteranceSpan);

/// <summary>A tool the host declared it cannot intercept (CV-4).</summary>
internal sealed record UncoveredSpec(string Tool, string Category);

/// <summary>The turn a step runs in — explicit in every property, never ambient (GT-2).</summary>
internal sealed record CtxSpec(
    string TenantId,
    string ConversationId,
    string Channel,
    PrincipalSpec? Principal,
    string? Utterance,
    string? MessageId);

/// <summary>Who is acting: a human-verified session, a machine caller, or an unresolved identity (AZ-2, AZ-3).</summary>
internal sealed record PrincipalSpec(string Kind, string Id, RelaySpec? Relay, string? AssertedMember);

/// <summary>The channel identity and message a relay is speaking for.</summary>
internal sealed record RelaySpec(string ChannelIdentity, string MessageId);
