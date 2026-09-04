using System.Text.Json.Nodes;

namespace Affiant.Conformance.Tests.Model;

/// <summary>
/// One act — the eight step kinds of <c>RUNNER.md</c> §3. The gate's whole surface is reachable
/// from these, so a fixture about a decision and a fixture about a filing differ in their steps,
/// not in their format.
/// </summary>
internal sealed record StepSpec(
    string Kind,
    string? As,
    DateTimeOffset? At,
    PrincipalSpec? Principal,
    bool PrincipalStated,
    string? TenantId,
    string? ConversationId,
    string? Entry,
    string? Refusal,
    bool RefusalStated,
    JsonObject Raw)
{
    // wrap-execute
    public ToolSpec? Tool { get; init; }

    /// <summary>The field-name to value map the model passed to a wrapped tool.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Args { get; init; }

    // file
    public string? ToolName { get; init; }

    public OperationSpec? Operation { get; init; }

    public IReadOnlyList<PreparedFieldSpec>? PreparedFields { get; init; }

    public IReadOnlyList<ToolFieldSpec>? Schema { get; init; }

    public string? OperationLabel { get; init; }

    // decide
    public DecisionSpec? Decision { get; init; }

    // markExecuted
    public string? Outcome { get; init; }

    public string? Detail { get; init; }

    // expireDue
    public int? Limit { get; init; }

    // rehydrate
    public PageSpec? Page { get; init; }

    // expireDue, rehydrate
    public ScopeSpec? Scope { get; init; }
}

/// <summary>A wrapped tool as a fixture describes it (GT-6, CV-4).</summary>
internal sealed record ToolSpec(
    string Name,
    string? Description,
    string EntityType,
    string? EntityId,
    bool WriteCapable,
    string? ExecutedBy,
    bool HostedMcp,
    bool OmitExecute,
    string? OperationLabel,
    IReadOnlyList<ToolFieldSpec> Fields);

/// <summary>One declared field of a tool: the rendering hint and the presentation constraints.</summary>
internal sealed record ToolFieldSpec(
    string Name,
    string Kind,
    string? Description,
    bool Required,
    IReadOnlyList<string>? AllowedValues,
    string? Pattern);

/// <summary>One create-or-update against one entity. The create branch is what turns into a null previous value on every field (AF-3).</summary>
internal sealed record OperationSpec(string Kind, string EntityType, string? EntityId, IReadOnlyList<string> Fields);

/// <summary>A field the host has already tagged, for a capture whose provenance is settled.</summary>
internal sealed record PreparedFieldSpec(
    string Name,
    string Kind,
    JsonNode? Value,
    bool IsMandatory,
    ProvenanceSpec? Provenance,
    bool ProvenanceStated);

/// <summary>The tag on a prepared field. Absent means "proposed, provenance unknown", which is a real state (AF-1).</summary>
internal sealed record ProvenanceSpec(string Source, double Confidence, BindingSpec? Binding, string? Note);

/// <summary>Approve, amend or reject (DK-1, AZ-1, AZ-2).</summary>
internal sealed record DecisionSpec(string Kind, IReadOnlyDictionary<string, JsonNode?>? Amendments, bool AmendmentsStated, string? Reason);

/// <summary>One page of a rehydration (DK-5).</summary>
internal sealed record PageSpec(int Limit, string? Cursor);

/// <summary>The tenant and conversation a sweep or a rehydration is scoped to.</summary>
internal sealed record ScopeSpec(string? TenantId, string? ConversationId);
