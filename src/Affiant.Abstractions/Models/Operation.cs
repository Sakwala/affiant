namespace Affiant.Abstractions.Models;

public sealed record Operation(string Kind)
{
    public static readonly Operation ReadQuery    = new("ReadQuery");
    public static readonly Operation WriteCreate  = new("WriteCreate");
    public static readonly Operation WriteUpdate  = new("WriteUpdate");
    public static readonly Operation WriteDelete  = new("WriteDelete");
}
