namespace Affiant.Abstractions.Models;

public sealed record Operation(string Kind)
{
    public static readonly Operation ReadQuery    = new("ReadQuery");
    public static readonly Operation WriteCreate  = new("WriteCreate");
    public static readonly Operation WriteUpdate  = new("WriteUpdate");
    public static readonly Operation WriteDelete  = new("WriteDelete");

    /// <summary>
    /// Whether <paramref name="operationType"/> names an <b>update-shaped</b> operation: one that
    /// changes an entity that already exists, and therefore names that entity and swears to what
    /// each field replaces.
    ///
    /// <para>
    /// Recognised spellings are <see cref="WriteUpdate"/>'s <c>"WriteUpdate"</c> and the bare
    /// <c>"update"</c>, both case-insensitively; everything else — including
    /// <see cref="WriteCreate"/> and a host's own verb — is create-shaped. The two spellings are
    /// both accepted because the framework's own operation vocabulary is four-valued
    /// (<c>ReadQuery</c>, <c>WriteCreate</c>, <c>WriteUpdate</c>, <c>WriteDelete</c>) while the
    /// shape an Affidavit swears to is two-valued: a create names no entity and swears to no
    /// previous values, an update names one and does. A host's own verb ("UpdateCustomer",
    /// "ApproveInvoice") travels beside the shape, never instead of it.
    /// </para>
    ///
    /// <para>
    /// <see cref="WriteDelete"/> is deliberately <em>not</em> update-shaped here. It names an
    /// existing entity, but "what a delete swears to" is not settled by the framework's current
    /// contracts and no shipped path produces one; treating it as an update would make the
    /// projection demand previous values for an operation nobody has defined the fields of.
    /// </para>
    /// </summary>
    public static bool IsUpdateShaped(string? operationType) =>
        string.Equals(operationType, WriteUpdate.Kind, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(operationType, "update", StringComparison.OrdinalIgnoreCase);
}
