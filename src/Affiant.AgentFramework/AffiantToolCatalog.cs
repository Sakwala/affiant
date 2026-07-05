namespace Affiant.AgentFramework;

using System.Reflection;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.AI;

/// <summary>
/// One reflection pass over a tool type producing both the <see cref="AIFunction"/>s MAF invokes
/// and the <see cref="AffiantToolDescriptor"/>s the neutral pipeline reads — the same descriptor
/// shape and <see cref="AffiantWriteToolAttribute"/> the Semantic Kernel adapter's plugin walker
/// produces, so both backends register identical tool metadata for an equivalent tool type.
///
/// Each produced <see cref="AIFunction"/> resolves its invocation target from
/// <see cref="AIFunctionArguments.Services"/> at call time rather than from a supplied instance,
/// so <typeparamref name="T"/> is not constructed here — the host registers <typeparamref name="T"/>
/// in its own DI container and MAF supplies the per-invocation service provider via
/// <c>ChatClientAgent(..., services:)</c>.
/// </summary>
public sealed record AffiantToolCatalog(
    IReadOnlyList<AIFunction> Functions,
    IReadOnlyList<AffiantToolDescriptor> Descriptors)
{
    public static AffiantToolCatalog FromType<T>(string? pluginName = null) where T : class
    {
        pluginName ??= typeof(T).Name;

        var functions = new List<AIFunction>();
        var descriptors = new List<AffiantToolDescriptor>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsGenericMethodDefinition) continue;
            if (method.DeclaringType == typeof(object)) continue;
            if (method.IsSpecialName) continue; // property accessors, event add/remove

            if (!seenNames.Add(method.Name))
                throw new InvalidOperationException(
                    $"AffiantToolCatalog.FromType<{typeof(T).Name}>(): method '{method.Name}' is overloaded. " +
                    "Tool method overloads are not supported — a tool is identified by (function name, plugin " +
                    "name), so overloads collapse to duplicate descriptors that later fail at registry time. " +
                    $"Rename one overload so each tool method on {typeof(T).Name} has a unique name.");

            functions.Add(AIFunctionFactory.Create(method, ResolveTarget<T>));

            var write = method.GetCustomAttribute<AffiantWriteToolAttribute>();
            descriptors.Add(write is null
                ? new AffiantToolDescriptor(method.Name, pluginName, Operation.ReadQuery, null, null)
                : new AffiantToolDescriptor(
                    method.Name, pluginName,
                    new Operation(write.Operation),
                    write.EntityType,
                    write.InferenceStrategy));
        }

        return new AffiantToolCatalog(functions, descriptors);
    }

    private static object ResolveTarget<T>(AIFunctionArguments arguments) where T : class =>
        (arguments.Services?.GetService(typeof(T)) as T)
            ?? throw new InvalidOperationException(
                $"AffiantToolCatalog.FromType<{typeof(T).Name}>(): no {typeof(T).Name} instance was resolvable " +
                $"from the invocation's service provider. Register {typeof(T).Name} in the host's DI container " +
                "(e.g. services.AddScoped<...>()) and construct the agent with that service provider " +
                "(new ChatClientAgent(chatClient, ..., services: serviceProvider)).");
}
