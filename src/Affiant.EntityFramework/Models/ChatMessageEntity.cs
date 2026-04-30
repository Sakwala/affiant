namespace Affiant.EntityFramework.Models;

public class ChatMessageEntity
{
    public long MessageId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? AuthorName { get; set; }
    public string? ModelId { get; set; }
    public string? ToolCallId { get; set; }
    public string? FunctionName { get; set; }
    public string? ArgumentsJson { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
