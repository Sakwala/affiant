namespace Affiant.Abstractions.Models;

using System.Collections.ObjectModel;

/// <summary>
/// The extracted state of a single <c>Projected == false</c> <see cref="TaskInferenceField"/> —
/// the value the LLM (or a downstream merge) put into <c>ContextFabric</c> for that field name,
/// together with its full <see cref="ProvenanceChain"/>. Extraction fields never become an
/// <c>AffidavitField</c>; this is the only place their extracted state is observable, and only
/// to <see cref="Affiant.Abstractions.Interfaces.IFieldResolver"/> implementations via
/// <see cref="FieldResolutionContext.Facts"/> — never serialized toward reviewer clients.
/// </summary>
public sealed record ExtractionFact(object? Value, ProvenanceChain Chain);

/// <summary>
/// The set of <see cref="ExtractionFact"/>s for the current projection, keyed by
/// <see cref="TaskInferenceField.Name"/>. A field with no fabric state yet (nothing extracted
/// for it this turn) is simply absent from the dictionary rather than present with a null
/// chain — mirrors <c>IContextFabric.GetFieldChain</c> returning <c>null</c> for "no chain yet".
///
/// Never a member of <c>Affidavit</c> and never serialized toward reviewer clients — see
/// <see cref="ExtractionFact"/>.
/// </summary>
public sealed class ExtractionFacts : ReadOnlyDictionary<string, ExtractionFact>
{
    public ExtractionFacts(IDictionary<string, ExtractionFact> facts) : base(facts)
    {
    }
}
