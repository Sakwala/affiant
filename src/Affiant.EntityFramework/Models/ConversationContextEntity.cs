namespace Affiant.EntityFramework.Models;

public class ConversationContextEntity
{
    public string SessionId { get; set; } = string.Empty;
    public string EntitiesJson { get; set; } = "{}";
    public string FieldValuesJson { get; set; } = "{}";
    public string ProvenanceChainsJson { get; set; } = "{}";
    public DateTimeOffset LastUpdatedAt { get; set; }
}
