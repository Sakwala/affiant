namespace QuickstartHost.Agent;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

/// <summary>
/// The field schema for the <c>LeaveRequest</c> write domain. One strategy serves both write
/// tools in this sample — <c>request_leave</c> (create) and <c>amend_leave</c> (update) — because
/// both propose the same entity's fields; only the operation and the presence of a previous value
/// differ.
///
/// The framework reads three things from this type: the entity name (used as both the
/// <c>EntityType</c> and the <c>ContextFabric</c> key for the entity's accumulated state), the
/// field list (which fields exist, their JSON type, constraints and whether they are mandatory),
/// and the confidence floor. <see cref="Projection.LeaveAffidavitProjection"/> iterates
/// <see cref="Fields"/> in declared order, so this list also fixes the order a reviewer sees them
/// on the Evidence Card.
/// </summary>
public sealed class LeaveTaskInferenceStrategy : ITaskInferenceStrategy
{
    /// <summary>The <c>ContextFabric</c> key and the <c>Affidavit.EntityType</c> for this domain.</summary>
    public const string LeaveRequestEntity = "LeaveRequest";

    /// <summary>
    /// The <c>EntityRef.Fields</c> key the sample uses to carry the real database id of the row an
    /// update targets.
    ///
    /// It cannot travel on <c>EntityRef.EntityId</c>: <c>ContextFabric</c> keys entities by
    /// <c>EntityId</c>, and every projection — the framework's default
    /// <c>SchemaDrivenAffidavitProjection</c> included — looks the entity up by the strategy's
    /// entity <em>name</em>. Putting a row id there would make the entity unfindable. The
    /// framework itself uses the same "real id travels as a named field" idiom for the
    /// conversation marker entity.
    /// </summary>
    public const string EntityIdField = "EntityId";

    public string EntityName => LeaveRequestEntity;

    public double? MinimumConfidenceThreshold => 0.5;

    public IReadOnlyList<TaskInferenceField> Fields { get; } =
    [
        new("Employee", "string", "Who the leave is for — the employee's full name.",
            MaxLength: 200, Required: true),
        new("StartDate", "string", "First day of leave (yyyy-MM-dd).",
            Pattern: @"^\d{4}-\d{2}-\d{2}$", Required: true, Format: "date"),
        new("EndDate", "string", "Last day of leave (yyyy-MM-dd), inclusive.",
            Pattern: @"^\d{4}-\d{2}-\d{2}$", Required: true, Format: "date"),
        new("LeaveType", "string", "Type of leave.",
            Enum: ["Annual", "Sick", "Personal"], Required: true),
        new("Days", "integer", "Working days this leave uses up."),
        new("Reason", "string", "Why the leave is being requested.",
            MaxLength: 1000, Required: true),
    ];
}
