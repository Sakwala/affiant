namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// Legacy deterministic-value source for one card field. Superseded by
/// <see cref="IFieldResolver"/>, which is async (supports I/O-bound lookups and DI-scoped
/// dependencies) and returns the resolved value alongside its <see cref="ProvenanceTag"/>
/// instead of a bare tag whose value must already sit in <c>ContextFabric</c>'s entity.
///
/// Kept fully functional — existing hosts implementing this interface compile and behave
/// exactly as before, including the fix to chain-truncation described on
/// <c>SchemaDrivenAffidavitProjection</c>. New code should implement <see cref="IFieldResolver"/>
/// instead. Scheduled for removal in 1.0.0-beta.2, once the Meridian reference app's
/// <c>AircraftLocationFieldSource</c> has been migrated — tracked as affiant#37. The deferral is a
/// recorded decision (architecture review Area 8, ruling 5), not an oversight: every other
/// deprecated-on-arrival member was deleted in that same pass, and this one was excepted only
/// because a live worked example still implements it.
/// </summary>
[Obsolete(
    "Use IFieldResolver instead — it is async and returns the resolved value alongside its " +
    "ProvenanceTag rather than assuming the value already sits in ContextFabric's entity. " +
    "IDeterministicFieldSource keeps working (projection precedence: resolver, then legacy " +
    "source, then fabric chain, then Empty) but will be removed in a future major version.",
    error: false)]
public interface IDeterministicFieldSource
{
    string FieldName { get; }

    ProvenanceTag? Resolve(IContextFabric fabric);
}
