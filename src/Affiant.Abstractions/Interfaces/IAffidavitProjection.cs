namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// Builds the <see cref="Affidavit"/> for one entity type from the accumulated conversation state.
/// </summary>
public interface IAffidavitProjection
{
    /// <summary>The entity type this projection builds Affidavits for.</summary>
    string EntityType { get; }

    /// <summary>
    /// Project an <see cref="Affidavit"/> for <paramref name="operationType"/> from
    /// <paramref name="fabric"/>.
    /// </summary>
    /// <param name="fabric">The accumulated entity state and per-field provenance for the turn.</param>
    /// <param name="operationType">
    /// The operation being proposed. <see cref="Operation.IsUpdateShaped"/> decides whether it is
    /// update-shaped, which is what makes <paramref name="entityId"/> required or forbidden.
    /// </param>
    /// <param name="warnings">Business-rule warnings to carry onto the Affidavit.</param>
    /// <param name="entityId">
    /// The entity an update-shaped operation targets — non-null <b>if and only if</b>
    /// <paramref name="operationType"/> is update-shaped. An implementation that is handed one
    /// without the other refuses rather than guessing: a create-shaped Affidavit filed for an
    /// update is exactly the defect this parameter exists to close, and an Affidavit that names an
    /// entity for a create swears to a relationship that does not exist.
    ///
    /// <para>
    /// Defaulted to <c>null</c> so a create-only caller reads unchanged. It is a parameter rather
    /// than something read out of <paramref name="fabric"/> because only the caller knows which
    /// entity the operation targets: the fabric keys entities by id and has no notion of "the one
    /// being written".
    /// </para>
    /// </param>
    Affidavit Project(
        IContextFabric fabric,
        string operationType,
        IReadOnlyList<string> warnings,
        string? entityId = null);
}
