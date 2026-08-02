namespace Affiant.AgentFramework.Attributes;

/// <summary>
/// Overrides the LLM-visible name <see cref="AffiantToolCatalog.FromType{T}"/> assigns to a tool
/// method, independent of the method's C# name.
///
/// Fills the gap tracked as
/// <see href="https://github.com/Sakwala/affiant/issues/16">affiant#16</see>: Semantic Kernel's
/// <c>[KernelFunction("name")]</c> already lets a host declare
/// <c>[KernelFunction(ToolNames.SearchThing)]</c> — the attribute site, the prompt-visible name,
/// and every other reference (telemetry, extractor matching, provenance tags) share one
/// <c>ToolNames</c> constant symbol. <see cref="AffiantToolCatalog.FromType{T}"/> had no equivalent,
/// which forced a host in the framework's first MAF migration wave to rename its C# methods
/// themselves to snake_case just to get a deliberate, constant-backed tool name — a workaround,
/// not the standard. This attribute closes that gap: apply it with a <c>ToolNames</c> constant and
/// keep a conventional PascalCase C# method name.
///
/// <code>
/// public sealed class ThingPlugin
/// {
///     [AffiantToolName(ToolNames.SearchThing)]
///     public string SearchThing(string query) { ... }
/// }
/// </code>
///
/// The constructor parameter is a plain <see langword="string"/> rather than anything more
/// structured because attribute arguments must be compile-time constants — the same constraint
/// <c>[KernelFunction(string)]</c> and <c>[AffiantWriteTool(string, string, Type)]</c> already
/// accept, which is what lets a host feed a <c>public const string</c> <c>ToolNames</c> member
/// directly into the attribute site.
///
/// A method with no <see cref="AffiantToolNameAttribute"/> is unaffected: its tool name is the bare
/// C# method name, exactly as before this attribute existed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AffiantToolNameAttribute(string name) : Attribute
{
    /// <summary>The LLM-visible tool name to use in place of the method's C# name.</summary>
    public string Name { get; } = name;
}
