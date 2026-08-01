namespace Affiant.Abstractions.Models;

using Affiant.Abstractions.Interfaces;

/// <summary>
/// The ambient state an <see cref="IFieldResolver"/> resolves against: the same
/// <see cref="IContextFabric"/> the projection itself reads, plus the current turn's
/// <see cref="ExtractionFacts"/> (the extracted state of every <c>Projected == false</c>
/// field — see <see cref="ExtractionFact"/>). Facts are never part of the emitted
/// <c>Affidavit</c>; this context is the only place a resolver can see them.
/// </summary>
public sealed record FieldResolutionContext(IContextFabric Fabric, ExtractionFacts Facts);

/// <summary>
/// The result of an <see cref="IFieldResolver"/> resolving one field: the value to place on
/// the Evidence Card and the <see cref="ProvenanceTag"/> swearing to its origin. Both travel
/// together deliberately — unlike the legacy <see cref="IDeterministicFieldSource"/>, which
/// returned only a tag and assumed the value already sat in <c>ContextFabric</c>'s entity, a
/// resolver may compute a value that was never itself written to the fabric (e.g. derived from
/// an <see cref="ExtractionFact"/> plus business logic).
/// </summary>
public sealed record FieldResolution(object? Value, ProvenanceTag Tag);
