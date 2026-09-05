namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// The host's domain write port: the one place an approved <see cref="Affidavit"/> becomes a real
/// mutation in the host's own system of record. The framework never writes domain data itself — it
/// gates, files, and evidences the write, then hands the approved affidavit here.
/// </summary>
/// <remarks>
/// <para>
/// Implement this once per host and register it in DI. It is called only after a review has been
/// granted (or auto-approved by a standing order), so an implementation may assume authorization
/// has already happened and must not re-run policy.
/// </para>
/// <para>
/// <b>Do not hand-roll the amendment fold.</b> <see cref="AffidavitAmendments.Apply"/> is the one
/// implementation of what an accepted correction does to the record — the reviewer's values, a
/// <see cref="ProvenanceSource.UserStated"/> tag carrying a
/// <see cref="ProvenanceBinding.ReviewerAct"/> binding appended on top of each amended field's
/// chain (never replacing the machine's tag beneath it), the field-list rules for a cleared field,
/// and all three confidence numbers recomputed. The gate already returns exactly that record on
/// <see cref="ReviewOutcome.Approved.AmendedAffidavit"/>; an implementation that folds the
/// amendments itself gets a different answer, and a card that shows the machine's pre-correction
/// confidence for a value a human already fixed is the defect this closes.
/// </para>
/// </remarks>
public interface IWriteExecutor
{
    /// <summary>
    /// Execute an approved write operation.
    /// Use the Affidavit.OperationType to route to the correct domain handler,
    /// apply any amendments, then persist. Raise on failure — the ReviewGate does not retry.
    /// </summary>
    /// <param name="amendments">
    /// The reviewer's field edits, or <c>null</c> when the write was approved unchanged. Values are
    /// nullable: an entry present with a <c>null</c> value means "clear this field", which is
    /// distinct from the key being absent ("leave this field alone"). Implementations must honor
    /// that distinction — collapsing the two is exactly the silent-amendment-loss bug the Area-8
    /// amendments unification exists to prevent.
    /// </param>
    Task<string?> ExecuteAsync(
        Affidavit affidavit, IReadOnlyDictionary<string, object?>? amendments, CancellationToken ct);
}
