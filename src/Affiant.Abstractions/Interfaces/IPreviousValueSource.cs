namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

/// <summary>
/// The host's read port for "what does the entity hold right now?" — the one thing an
/// update-shaped <see cref="Affidavit"/> needs that only the host's own system of record can
/// answer.
///
/// <para>
/// <b>Why this exists.</b> An update-shaped Affidavit names the entity it updates and carries a
/// <see cref="AffidavitField.PreviousValue"/> on every proposed field, so a reviewer can see
/// exactly what is changing rather than only what is being written. The framework never reads the
/// host's domain store — it gates, files and evidences the write — so the previous values have to
/// arrive through a port. Before this port existed, the built-in projection hard-coded every
/// previous value to null and every Affidavit it built was create-shaped, which made the promise
/// that a field's previous value shows "exactly what is changing" unmeetable without a host writing
/// a complete replacement projection.
/// </para>
///
/// <para>
/// <b>When it is consulted.</b> Updates only. A create has no stored values to report, and the
/// built-in projection never calls this port for one.
/// </para>
///
/// <para>
/// <b>Registration.</b> <c>services.AddPreviousValueSource&lt;TSource&gt;()</c>. More than one may
/// be registered — for a host that keeps different entity types in different stores — and the
/// projection consults them in registration order, taking the first non-null answer. A host whose
/// write tools declare update operations and registers none fails at startup rather than silently
/// filing create-shaped Affidavits for updates.
/// </para>
/// </summary>
public interface IPreviousValueSource
{
    /// <summary>
    /// The entity's stored field values before the proposed write, keyed by field name.
    /// </summary>
    /// <param name="entityType">The kind of entity, as the host names it.</param>
    /// <param name="entityId">The entity being updated. Never null — this port is for updates.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>
    /// A map of field name to stored value, or <c>null</c> when this source does not serve
    /// <paramref name="entityType"/> (so the projection can try the next registered source).
    ///
    /// <para>
    /// A field the entity has no stored value for may be absent from the map or present holding
    /// <c>null</c> — both mean "there was nothing there", and the projection records
    /// <c>null</c> for it either way. An <b>empty map</b> is a real answer: "this source owns the
    /// entity type and the entity holds nothing yet", which is different from the <c>null</c> that
    /// means "not mine, ask someone else".
    /// </para>
    /// </returns>
    Task<IReadOnlyDictionary<string, object?>?> GetPreviousValuesAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken);
}
