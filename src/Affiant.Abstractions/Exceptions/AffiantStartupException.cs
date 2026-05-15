namespace Affiant.Abstractions.Exceptions;

public sealed class AffiantStartupException : Exception
{
    public AffiantStartupException() { }
    public AffiantStartupException(string message) : base(message) { }
    public AffiantStartupException(string message, Exception inner) : base(message, inner) { }
}
