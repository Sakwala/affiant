namespace QuickstartHost.Agent;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;

/// <summary>
/// The one place this sample turns a caller's stated values into an <c>Affidavit</c>: it records
/// them on a <c>ContextFabric</c> with the provenance they actually have, then asks the registered
/// <c>IAffidavitProjection</c> for this entity type to project the affidavit.
///
/// <para>
/// Both write tools and the development seam go through here, so a card filed by a live model turn
/// and a card filed by the seam cannot drift: same fabric shape, same projection, same field
/// metadata. Nothing in this sample builds an <c>Affidavit</c> by hand.
/// </para>
///
/// <para>
/// <b>Why a fresh fabric per proposal.</b> The framework registers a conversation-scoped
/// <c>IContextFabric</c> that accumulates state across a turn, and a host using deferred inference
/// would build its proposal from that instance. This host does not: every value on the card comes
/// straight off the tool call's own arguments, so there is nothing accumulating and no reason to
/// reach outside the one proposal being built. It also keeps this type free of scoped
/// dependencies, which matters because Semantic Kernel creates a plugin instance once, from the
/// root service provider — a plugin whose dependency chain reaches a scoped service does not
/// start.
/// </para>
///
/// <para>
/// The projection is looked up by <c>IAffidavitProjection.EntityType</c> rather than injected
/// concretely, which is what makes the DI registration load-bearing — the framework's own
/// compliance harness resolves a projection the same way.
/// </para>
/// </summary>
public sealed class LeaveProposalBuilder(IEnumerable<IAffidavitProjection> projections)
{
    /// <summary>The <c>Affidavit.OperationType</c> for a proposal that creates a new row.</summary>
    public const string CreateOperation = "create";

    /// <summary>The <c>Affidavit.OperationType</c> for a proposal that changes an existing row.</summary>
    public const string UpdateOperation = "update";

    private IAffidavitProjection Projection =>
        projections.FirstOrDefault(p => p.EntityType == LeaveTaskInferenceStrategy.LeaveRequestEntity)
        ?? throw new InvalidOperationException(
            "No IAffidavitProjection is registered for entity type " +
            $"'{LeaveTaskInferenceStrategy.LeaveRequestEntity}'. Call " +
            "services.AddAffidavitProjection<LeaveAffidavitProjection>() during DI setup.");

    /// <summary>
    /// Records a create's stated field values and projects the affidavit. No entity id, so the
    /// projection leaves it and every previous value null.
    /// </summary>
    public Affidavit BuildCreate(IReadOnlyDictionary<string, string> statedFields) =>
        Build(CreateOperation, statedFields, leaveRequestId: null);

    /// <summary>
    /// Records an update's stated field values against an existing row and projects the affidavit.
    /// The projection reads that row, so the resulting card carries the entity's id and, per field,
    /// the value the database holds today.
    /// </summary>
    public Affidavit BuildUpdate(int leaveRequestId, IReadOnlyDictionary<string, string> statedFields) =>
        Build(UpdateOperation, statedFields, leaveRequestId);

    private Affidavit Build(
        string operationType,
        IReadOnlyDictionary<string, string> statedFields,
        int? leaveRequestId)
    {
        ArgumentNullException.ThrowIfNull(statedFields);

        var fabric = new ContextFabric();

        var entityFields = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, value) in statedFields)
            entityFields[name] = value;

        if (leaveRequestId is { } id)
            entityFields[LeaveTaskInferenceStrategy.EntityIdField] = id;

        fabric.Upsert(new EntityRef(
            EntityType: LeaveTaskInferenceStrategy.LeaveRequestEntity,
            // The fabric keys entities by EntityId and every projection looks this domain up by the
            // strategy's entity name, so the name is the key. The real row id travels as a field —
            // see LeaveTaskInferenceStrategy.EntityIdField.
            EntityId: LeaveTaskInferenceStrategy.LeaveRequestEntity,
            DisplayName: "Leave request",
            Fields: entityFields));

        // Every value here came straight off the caller's own arguments, so every tag is UserStated
        // and binds to the control the person typed into (PV-3). A field the caller said nothing
        // about gets no chain at all, and the projection decides between the record's current value
        // and ProvenanceTag.Empty.
        foreach (var name in statedFields.Keys)
        {
            fabric.SetFieldChain(name, ProvenanceChain.From(
                ProvenanceTag.FromUser(name, new ProvenanceBinding.FormInput(new FormInputRef(name)))));
        }

        return Projection.Project(
            fabric,
            operationType,
            [],
            leaveRequestId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
