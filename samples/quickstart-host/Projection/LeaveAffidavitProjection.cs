namespace QuickstartHost.Projection;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuickstartHost.Agent;
using QuickstartHost.Data;

/// <summary>
/// The host's own <c>IAffidavitProjection</c> for the <c>LeaveRequest</c> domain: it turns the
/// accumulated <c>ContextFabric</c> state for one turn into the <c>Affidavit</c> a reviewer sees.
///
/// <para>
/// <b>Why this sample has one at all.</b> The framework ships a default projection,
/// <c>SchemaDrivenAffidavitProjection</c>, which reads every field from the fabric and is enough
/// for a create. It hard-codes two things to <c>null</c> that only the host can know:
/// <c>Affidavit.EntityId</c> — which row is being changed — and each
/// <c>AffidavitField.PreviousValue</c> — what that row says today. Without both, an update-shaped
/// write reaches a reviewer looking exactly like a create: five proposed values and no way to see
/// which of them actually change anything. Reading them requires the host's own database, which a
/// domain-agnostic framework type cannot touch. So the host supplies the projection and the
/// framework consumes it through the same interface.
/// </para>
///
/// <para>
/// <b>Create versus update.</b> The two paths differ by one thing: whether the fabric's entity
/// carries <see cref="LeaveTaskInferenceStrategy.EntityIdField"/>. When it does, this projection
/// loads that row and stamps <c>EntityId</c> on the affidavit plus a <c>PreviousValue</c> on every
/// field; when it does not, both stay <c>null</c> exactly as they should for a create.
/// </para>
///
/// <para>
/// <b>Provenance on an update.</b> A field the caller actually asked to change carries whatever
/// chain the caller recorded in the fabric (<c>UserStated</c>, for a tool call's own arguments).
/// A field the caller said nothing about is still proposed — an affidavit describes the whole row
/// after the write, not a patch — and carries an <c>External</c> tag naming the database as its
/// source. That is Rule 7 in practice: nothing is omitted, and nothing claims a user said it when
/// the database did.
/// </para>
///
/// <para>
/// <b>Lifetime.</b> <c>AddAffidavitProjection&lt;T&gt;()</c> registers a projection as a
/// singleton, so this type never injects a <c>DbContext</c> directly — that would be a captive
/// scoped dependency. It opens a scope per projection instead. <c>Project</c> is synchronous by
/// contract, so the read below uses EF's synchronous API rather than blocking on an async one.
/// </para>
/// </summary>
public sealed class LeaveAffidavitProjection(
    LeaveTaskInferenceStrategy strategy,
    IServiceScopeFactory scopeFactory) : IAffidavitProjection
{
    private static readonly ProvenanceTag FromRecord = new(
        ProvenanceSource.External,
        Confidence: 0.95f,
        Evidence: "Read from the existing leave request",
        ConversationTurn: null);

    public string EntityType => strategy.EntityName;

    public Affidavit Project(
        IContextFabric fabric,
        string operationType,
        IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(warnings);

        var entity = fabric.GetByKey(strategy.EntityName);
        var entityId = ReadEntityId(entity);
        var existing = entityId is null ? null : LoadLeaveRequest(entityId.Value);

        var fields = strategy.Fields
            .Select(field => ProjectField(field, fabric, entity, existing))
            .ToArray();

        var sourced = fields
            .Where(f => f.Provenance.Current.Source != ProvenanceSource.Empty)
            .ToArray();

        var allWarnings = warnings
            .Concat(fields
                .Where(f => f.IsMandatory && IsBlank(f.Value))
                .Select(f => $"{f.Name} is required and has no value — a reviewer must supply one."))
            .ToArray();

        return new Affidavit(
            OperationType: operationType,
            EntityType: strategy.EntityName,
            EntityId: existing?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Fields: fields,
            AggregateConfidence: sourced.Length == 0
                ? 0f
                : sourced.Average(f => f.Provenance.Current.Confidence),
            Warnings: allWarnings,
            RequiresConfirmation: true);
    }

    private AffidavitField ProjectField(
        TaskInferenceField field,
        IContextFabric fabric,
        EntityRef? entity,
        LeaveRequest? existing)
    {
        var previousValue = existing is null ? null : ReadFromRecord(existing, field.Name);
        var proposedValue = entity is not null && entity.Fields.TryGetValue(field.Name, out var v)
            ? v
            : null;

        // An affidavit states the whole row as it would stand after the write, so a field the
        // caller left alone still carries the record's current value — with the record, not the
        // caller, named as its source.
        var value = proposedValue ?? previousValue;

        var chain = fabric.GetFieldChain(field.Name)
            ?? (previousValue is not null
                ? ProvenanceChain.From(FromRecord)
                // Rule 7: a field with no known provenance is tagged Empty, never dropped.
                : ProvenanceChain.From(ProvenanceTag.Empty));

        var (kind, allowedValues) = ClassifyKind(field);

        return new AffidavitField(
            Name: field.Name,
            Value: value,
            PreviousValue: previousValue,
            Provenance: chain,
            IsMandatory: field.Required,
            Kind: kind,
            AllowedValues: allowedValues,
            Pattern: field.Pattern);
    }

    private static int? ReadEntityId(EntityRef? entity)
    {
        if (entity is null)
            return null;
        if (!entity.Fields.TryGetValue(LeaveTaskInferenceStrategy.EntityIdField, out var raw))
            return null;

        return raw switch
        {
            int id => id,
            string text when int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private LeaveRequest? LoadLeaveRequest(int id)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HrDbContext>();
        return db.LeaveRequests.AsNoTracking().FirstOrDefault(r => r.Id == id);
    }

    private static object? ReadFromRecord(LeaveRequest record, string fieldName) => fieldName switch
    {
        "Employee" => record.Employee,
        "StartDate" => record.StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        "EndDate" => record.EndDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        "LeaveType" => record.LeaveType,
        "Days" => record.Days.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "Reason" => record.Reason,
        _ => null,
    };

    private static bool IsBlank(object? value) =>
        value is null || (value is string text && string.IsNullOrWhiteSpace(text));

    /// <summary>
    /// Derives the reviewer-UI rendering hint from the schema, using the same precedence the
    /// framework's own default projection applies: an explicit enum wins, then a numeric JSON
    /// type, then an explicit "date" format, else text.
    /// </summary>
    private static (string Kind, IReadOnlyList<string>? AllowedValues) ClassifyKind(TaskInferenceField field)
    {
        if (field.Enum is not null)
            return (AffidavitFieldKind.Enum, field.Enum);
        if (field.JsonType is "number" or "integer")
            return (AffidavitFieldKind.Number, null);
        if (field.Format == "date")
            return (AffidavitFieldKind.Date, null);
        return (AffidavitFieldKind.Text, null);
    }
}
