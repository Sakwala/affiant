namespace Affiant.EntityFramework.Models;

/// <summary>
/// EF-tracked row for the <c>affiant.Docket</c> table — the durable review queue.
/// Mutable counterpart to the immutable <see cref="Affiant.Abstractions.Models.DocketEntry"/>
/// domain record. JSON columns store Affidavit and provenance data; conversion happens
/// in the store implementations.
/// </summary>
public class DocketEntryEntity
{
    public Guid EntryId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string? ReviewerUserId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string AffidavitJson { get; set; } = "{}";
    public string ProvenanceChainsJson { get; set; } = "{}";
    public string? AmendmentsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = "Pending";
}
