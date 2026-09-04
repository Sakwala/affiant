namespace Affiant.Abstractions.Telemetry;

using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;

/// <summary>
/// The versioned telemetry-key registry: every event the gate emits, named once, with the
/// attribute names each event carries.
///
/// <para>
/// <b>This is a public, versioned API, not an implementation detail.</b> Operators build alerts
/// and dashboards on these names, so a key is never renamed and never removed — only deprecated,
/// with the replacement named in the deprecation message. Adding a key is a minor change; removing
/// or renaming one is a breaking change and needs a major version. <c>TelemetryKeysTests</c>
/// enforces the never-removed half against a snapshot list that is deliberately duplicated in the
/// test, so deleting a key here cannot silently delete the assertion that guards it.
/// </para>
///
/// <para>
/// <b>Attributes carry field names, never field values.</b> An event is an operational signal, not
/// an audit record; the audit record is the Affidavit. A field's <em>name</em> is schema and is
/// safe to emit; a field's <em>value</em> is the user's data and is not.
/// </para>
///
/// <para>
/// <b>Where the names come from.</b> The nine keys and their attribute lists are fixed by the
/// Affiant protocol rulebook, rule TL-1
/// (<see href="https://github.com/Sakwala/affiant-protocol/blob/main/INVARIANTS.md"/>, v0.1). Where
/// a public standard already names the same thing, the standard's name is used (rule TL-2):
/// OpenTelemetry's <c>gen_ai.*</c> semantic conventions supply <see cref="Attributes.GenAiToolName"/>,
/// <see cref="Attributes.GenAiConversationId"/> and <see cref="Attributes.GenAiOperationName"/>.
/// </para>
///
/// <para>
/// The registry ships twice, on purpose: as the constants on this class (what emitting code and a
/// compiler can see) and as an embedded <c>telemetry-keys.json</c> document conforming to the
/// rulebook's <c>telemetry-key.schema.json</c> (what a tool, a collector's config generator, or a
/// second implementation can read without running .NET). <see cref="Registry"/> reads the embedded
/// document; a test asserts the two agree and that the document validates against the schema.
/// </para>
/// </summary>
public static class TelemetryKeys
{
    /// <summary>An Affidavit was filed as a Docket entry.</summary>
    public const string AffidavitFiled = "affidavit.filed";

    /// <summary>A proposal was refused before filing because it swore to nothing (GT-3).</summary>
    public const string AffidavitRefusedSubstance = "affidavit.refused.substance";

    /// <summary>
    /// A tool the gate must cover could not be intercepted, or a tool the host declared uncovered
    /// produced a proposal (CV-4).
    /// </summary>
    public const string CoverageRefused = "coverage.refused";

    /// <summary>A Docket entry changed state (DK-1).</summary>
    public const string DocketTransition = "docket.transition";

    /// <summary>A pending Docket entry passed its expiry (DK-3).</summary>
    public const string DocketExpired = "docket.expired";

    /// <summary>
    /// A decision was refused: no resolved principal, another tenant, the host's authorization port
    /// said no, or the entry could not accept it (AZ-2).
    /// </summary>
    public const string DecisionUnauthorized = "decision.unauthorized";

    /// <summary>A Standing Order policy approved a write with no person present (AZ-1).</summary>
    public const string StandingOrderFired = "standing-order.fired";

    /// <summary>A Standing Order verdict was not honoured (GT-5, PV-4).</summary>
    public const string StandingOrderBlocked = "standing-order.blocked";

    /// <summary>
    /// A host's approval policy or the gate's own deadline configuration broke its contract:
    /// an unusable deadline, or an evaluate that threw (GT-4, CV-1).
    /// </summary>
    public const string PolicyInvalid = "policy.invalid";

    /// <summary>
    /// Every key, in registry order. The order only ever grows at the end — a consumer may index
    /// into this list and expect the same key at the same position in a later release.
    /// </summary>
    public static ImmutableArray<string> All { get; } =
    [
        AffidavitFiled,
        AffidavitRefusedSubstance,
        CoverageRefused,
        DocketTransition,
        DocketExpired,
        DecisionUnauthorized,
        StandingOrderFired,
        StandingOrderBlocked,
        PolicyInvalid,
    ];

    /// <summary>Whether <paramref name="key"/> names an event in the registry.</summary>
    public static bool Contains(string key) => All.Contains(key);

    /// <summary>
    /// The registry as shipped: the embedded <c>telemetry-keys.json</c> document, parsed once.
    /// Use it to enumerate a key's attribute names or the release a key first shipped in;
    /// use the constants above at an emitting call site.
    /// </summary>
    public static TelemetryKeyRegistry Registry => LazyRegistry.Value;

    private static readonly Lazy<TelemetryKeyRegistry> LazyRegistry = new(LoadRegistry);

    /// <summary>
    /// The resource name of the embedded registry document, for a host that wants to read the raw
    /// JSON out of the assembly rather than the parsed shape.
    /// </summary>
    public const string RegistryResourceName = "Affiant.Abstractions.Telemetry.telemetry-keys.json";

    private static TelemetryKeyRegistry LoadRegistry()
    {
        using var stream =
            typeof(TelemetryKeys).GetTypeInfo().Assembly.GetManifestResourceStream(RegistryResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded telemetry-key registry '{RegistryResourceName}' is missing from " +
                "Affiant.Abstractions. It is an EmbeddedResource in the csproj; a build that drops " +
                "it produces an assembly whose telemetry keys cannot be enumerated.");

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var entries = ImmutableArray.CreateBuilder<TelemetryKeyEntry>();
        foreach (var key in root.GetProperty("keys").EnumerateArray())
        {
            var attributes = ImmutableArray.CreateBuilder<string>();
            foreach (var attribute in key.GetProperty("attributes").EnumerateArray())
                attributes.Add(attribute.GetString()!);

            entries.Add(new TelemetryKeyEntry(
                key.GetProperty("key").GetString()!,
                key.GetProperty("since").GetString()!,
                key.GetProperty("description").GetString()!,
                attributes.ToImmutable()));
        }

        return new TelemetryKeyRegistry(
            root.GetProperty("protocolVersion").GetString()!,
            root.GetProperty("registryVersion").GetString()!,
            entries.ToImmutable());
    }

    /// <summary>
    /// The attribute names the registry's events carry. Names only — never a field value, an
    /// utterance, or a principal's identifier.
    /// </summary>
    public static class Attributes
    {
        /// <summary>OpenTelemetry <c>gen_ai.tool.name</c> (TL-2): the tool that proposed the write.</summary>
        public const string GenAiToolName = "gen_ai.tool.name";

        /// <summary>OpenTelemetry <c>gen_ai.conversation.id</c> (TL-2): the conversation the turn belongs to.</summary>
        public const string GenAiConversationId = "gen_ai.conversation.id";

        /// <summary>OpenTelemetry <c>gen_ai.operation.name</c> (TL-2): the operation being performed.</summary>
        public const string GenAiOperationName = "gen_ai.operation.name";

        /// <summary>The Docket entry's identifier.</summary>
        public const string EntryId = "entry.id";

        /// <summary>The requirement level the policy chain returned for the entry.</summary>
        public const string DocketRequirement = "docket.requirement";

        /// <summary>The entry's review status.</summary>
        public const string DocketStatus = "docket.status";

        /// <summary>How many fields the Affidavit swears to. A count, never the fields themselves.</summary>
        public const string AffidavitFieldCount = "affidavit.field_count";

        /// <summary>Whether the filing call created the entry, as opposed to replaying an existing one.</summary>
        public const string Created = "created";

        /// <summary>The state the entry left.</summary>
        public const string From = "from";

        /// <summary>The state the entry entered.</summary>
        public const string To = "to";

        /// <summary>The execution outcome carried on an approved row.</summary>
        public const string Execution = "execution";

        /// <summary>The kind of decision that caused the transition.</summary>
        public const string DecisionKind = "decision.kind";

        /// <summary>The kind of attestation written on the row.</summary>
        public const string AttestationKind = "attestation.kind";

        /// <summary>Whether the transition carried reviewer amendments.</summary>
        public const string Amended = "amended";

        /// <summary>A stable code an operator can alert on.</summary>
        public const string Reason = "reason";

        /// <summary>The kind of principal that acted — the kind, never the identifier.</summary>
        public const string PrincipalKind = "principal.kind";

        /// <summary>Which decision path refused: <c>decide</c>, <c>mark-executed</c> or <c>resubmit</c>.</summary>
        public const string Path = "path";

        /// <summary>The category of tool the gate could not cover.</summary>
        public const string CoverageCategory = "coverage.category";

        /// <summary>Where in the lifecycle a coverage refusal happened: <c>wire-up</c> or <c>proposal</c>.</summary>
        public const string Phase = "phase";

        /// <summary>The policy that fired, was blocked, or broke its contract.</summary>
        public const string PolicyId = "policy.id";

        /// <summary>The policy's own version, where the policy declares one.</summary>
        public const string PolicyVersion = "policy.version";

        /// <summary>
        /// The stable code for why a Standing Order was not honoured: <c>mandatory-field-empty</c>,
        /// <c>unbound-declared-input</c> or <c>risk-above-threshold</c>.
        /// </summary>
        public const string BlockedReason = "blocked.reason";

        /// <summary>The name of the field whose provenance blocked a Standing Order.</summary>
        public const string ProvenanceField = "provenance.field";

        /// <summary>The provenance source that blocked a Standing Order.</summary>
        public const string ProvenanceSource = "provenance.source";

        /// <summary>The names of the mandatory fields that read Empty. Names, never values.</summary>
        public const string AffidavitEmptyMandatoryFields = "affidavit.empty_mandatory_fields";

        /// <summary>The computed risk score.</summary>
        public const string RiskScore = "risk.score";

        /// <summary>The policy's declared risk threshold.</summary>
        public const string RiskThreshold = "risk.threshold";

        /// <summary>The configuration option whose value broke the contract.</summary>
        public const string Option = "option";
    }
}

/// <summary>The registry document as shipped beside the packages (rulebook rule TL-1).</summary>
/// <param name="ProtocolVersion">The protocol version the document conforms to (SR-4).</param>
/// <param name="RegistryVersion">
/// The registry's own version, numbered as this implementation numbers its releases. Distinct from
/// <paramref name="ProtocolVersion"/>: a registry gains keys between protocol versions.
/// </param>
/// <param name="Keys">Every key, in registry order.</param>
public sealed record TelemetryKeyRegistry(
    string ProtocolVersion,
    string RegistryVersion,
    ImmutableArray<TelemetryKeyEntry> Keys);

/// <summary>One event the gate emits.</summary>
/// <param name="Key">The event name.</param>
/// <param name="Since">The release of this implementation the key first shipped in.</param>
/// <param name="Description">What the event means, in one line.</param>
/// <param name="Attributes">
/// The attribute names this event carries. Names, never values. An event may carry a subset — an
/// attribute whose value this release cannot know is omitted rather than guessed.
/// </param>
public sealed record TelemetryKeyEntry(
    string Key,
    string Since,
    string Description,
    ImmutableArray<string> Attributes);
