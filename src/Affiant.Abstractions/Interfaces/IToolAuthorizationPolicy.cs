namespace Affiant.Abstractions.Interfaces;

public interface IToolAuthorizationPolicy
{
    /// <summary>
    /// Returns true if the user is authorized to invoke the specified tool.
    /// The ConversationContext provides accumulated session state for stateful decisions.
    /// </summary>
    Task<bool> AuthorizeAsync(string functionName, string userId, ConversationContext context);
}
