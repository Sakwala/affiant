namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// Resolves the card value for one <see cref="TaskInferenceField"/> from sources beyond raw
/// LLM inference — deterministic business logic, an authoritative lookup, or a value derived
/// from an <see cref="ExtractionFact"/> (see <see cref="FieldResolutionContext.Facts"/>).
///
/// Successor to <see cref="IDeterministicFieldSource"/> (now <c>[Obsolete]</c>): unlike that
/// interface, <see cref="ResolveAsync"/> is async (supporting I/O-bound lookups and DI-scoped
/// dependencies) and returns the resolved <c>Value</c> alongside its <see cref="ProvenanceTag"/>
/// in one <see cref="FieldResolution"/>, rather than a bare tag whose value must separately
/// already sit in <c>ContextFabric</c>.
///
/// Projection precedence (see <c>SchemaDrivenAffidavitProjection</c>): the first registered
/// resolver for a field name whose <see cref="ResolveAsync"/> returns non-null wins; if none
/// do, the legacy <see cref="IDeterministicFieldSource"/> is tried next, then the raw
/// ContextFabric chain, then <see cref="ProvenanceTag.Empty"/>.
///
/// Evidence honesty: implementations must describe their own derivation in
/// <see cref="FieldResolution.Tag"/>'s <see cref="ProvenanceTag.Evidence"/> rather than
/// hardcoding a tool or mechanism name that may not actually have run for a given resolution —
/// e.g. <c>"Resolved from tail number N12345 (stated in conversation)"</c>, not a blanket
/// <c>"Resolved by tool lookup"</c> that would be untrue when the value instead came from a
/// cache hit or a default.
/// </summary>
public interface IFieldResolver
{
    /// <summary>The <see cref="TaskInferenceField.Name"/> this resolver produces a value for.</summary>
    string FieldName { get; }

    /// <summary>
    /// Attempts to resolve <see cref="FieldName"/>'s card value. Returns <c>null</c> when this
    /// resolver has no opinion for the current <paramref name="context"/> — projection then
    /// falls through to the next resolver registered for the same field name, and beyond that
    /// to the legacy-source/fabric-chain/Empty fallback chain.
    /// </summary>
    Task<FieldResolution?> ResolveAsync(FieldResolutionContext context, CancellationToken cancellationToken);
}
