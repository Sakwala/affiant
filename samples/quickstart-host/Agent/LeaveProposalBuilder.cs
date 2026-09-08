namespace QuickstartHost.Agent;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;

/// <summary>
/// Where a proposal's values came from — the one thing that decides how they are graded.
/// </summary>
public enum ProposalOrigin
{
    /// <summary>
    /// A model produced the values as a write tool's arguments: it read the conversation and filled
    /// the tool's parameters. Nobody typed them, so nothing here may be graded
    /// <c>UserStated</c> (PV-3).
    /// </summary>
    ModelArguments,

    /// <summary>
    /// A person stated the values directly in a development-seam request
    /// (<c>POST /api/dev/propose</c>) — the one path in this sample where the caller and the person
    /// are the same.
    /// </summary>
    DevSeamRequest,
}

/// <summary>
/// The one place this sample turns a caller's values into an <c>Affidavit</c>: it records them on a
/// <c>ContextFabric</c> with the provenance they actually have, then asks the registered
/// <c>IAffidavitProjection</c> for this entity type to project the affidavit.
///
/// <para>
/// Both write tools and the development seam go through here, so a card filed by a live model turn
/// and a card filed by the seam cannot drift: same fabric shape, same projection, same field
/// metadata. Nothing in this sample builds an <c>Affidavit</c> by hand.
/// </para>
///
/// <para>
/// <b>Provenance follows the path, not the caller.</b> Both callers hand this type the same
/// dictionary of field values, and the two paths differ in the only way that matters: on
/// <see cref="ProposalOrigin.ModelArguments"/> a model extracted the values from the conversation,
/// on <see cref="ProposalOrigin.DevSeamRequest"/> a person wrote them into the request. So the
/// caller names its path and this type grades accordingly — see <c>Build</c>.
/// </para>
///
/// <para>
/// <b>Why a fresh fabric per proposal.</b> The framework registers a conversation-scoped
/// <c>IContextFabric</c> that accumulates state across a turn, and a host using deferred inference
/// would build its proposal from that instance. This host does not: every value on the card comes
/// straight off the call's own arguments, so there is nothing accumulating and no reason to
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

    /// <summary>
    /// The surface a <see cref="ProposalOrigin.DevSeamRequest"/> value arrived on, as the binding
    /// names it: the seam route, then the affidavit field the request stated. An auditor resolves it
    /// to a request this host answered, which is more than the name of a form control this sample
    /// does not have.
    /// </summary>
    public const string DevSeamSurface = "POST /api/dev/propose";

    /// <summary>
    /// The confidence this host defends for a value a model put in a write tool's argument: the
    /// framework's own default for an inference tag. The host watched nobody type the value and has
    /// nothing better to say about it than that a model produced it.
    /// </summary>
    public const float ModelArgumentConfidence = 0.6f;

    private IAffidavitProjection Projection =>
        projections.FirstOrDefault(p => p.EntityType == LeaveTaskInferenceStrategy.LeaveRequestEntity)
        ?? throw new InvalidOperationException(
            "No IAffidavitProjection is registered for entity type " +
            $"'{LeaveTaskInferenceStrategy.LeaveRequestEntity}'. Call " +
            "services.AddAffidavitProjection<LeaveAffidavitProjection>() during DI setup.");

    /// <summary>
    /// Records a create's field values and projects the affidavit. No entity id, so the
    /// projection leaves it and every previous value null.
    /// </summary>
    /// <param name="fields">The proposed values, by affidavit field name.</param>
    /// <param name="origin">Where those values came from; it decides how each one is graded.</param>
    public Affidavit BuildCreate(IReadOnlyDictionary<string, string> fields, ProposalOrigin origin) =>
        Build(CreateOperation, fields, origin, leaveRequestId: null);

    /// <summary>
    /// Records an update's field values against an existing row and projects the affidavit.
    /// The projection reads that row, so the resulting card carries the entity's id and, per field,
    /// the value the database holds today.
    /// </summary>
    /// <param name="leaveRequestId">The row the update targets.</param>
    /// <param name="fields">The proposed values, by affidavit field name.</param>
    /// <param name="origin">Where those values came from; it decides how each one is graded.</param>
    public Affidavit BuildUpdate(
        int leaveRequestId,
        IReadOnlyDictionary<string, string> fields,
        ProposalOrigin origin) =>
        Build(UpdateOperation, fields, origin, leaveRequestId);

    private Affidavit Build(
        string operationType,
        IReadOnlyDictionary<string, string> statedFields,
        ProposalOrigin origin,
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

        // How a value is graded follows from where it came from, and from nothing else (PV-3).
        //
        // ModelArguments: a write tool's arguments are what a model extracted from the conversation,
        // so the strongest grade they can carry is an inference — UserStated is not reachable from
        // here, and ProvenanceTag.FromInference cannot name it. This host establishes nothing about
        // where in the turn a value appeared, so the grade is Inferred and the tag carries no
        // binding: there is no artifact to point an auditor at. A host that does establish that the
        // value is literally present in the turn mints InferenceSource.Conversation with an
        // utterance-span binding instead.
        //
        // DevSeamRequest: a person wrote these values into the request themselves, so UserStated,
        // bound to the seam route and the field they arrived in.
        //
        // A field the caller said nothing about gets no chain at all, and the projection decides
        // between the record's current value and ProvenanceTag.Empty.
        foreach (var name in statedFields.Keys)
        {
            var tag = origin == ProposalOrigin.DevSeamRequest
                ? ProvenanceTag.FromUser(
                    name, new ProvenanceBinding.FormInput(new FormInputRef($"{DevSeamSurface}#{name}")))
                : ProvenanceTag.FromInference(
                    InferenceSource.Inferred, name, ModelArgumentConfidence);

            fabric.SetFieldChain(name, ProvenanceChain.From(tag));
        }

        return Projection.Project(
            fabric,
            operationType,
            [],
            leaveRequestId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
