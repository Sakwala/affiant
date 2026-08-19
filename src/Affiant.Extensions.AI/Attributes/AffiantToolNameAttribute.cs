namespace Affiant.Extensions.AI.Attributes;

/// <summary>
/// Overrides the LLM-visible name <see cref="AffiantToolCatalog.FromType{T}"/> assigns to a tool
/// method, independent of the method's C# name.
///
/// <para>
/// Copied from <c>src/Affiant.AgentFramework/Attributes/AffiantToolNameAttribute.cs</c> (design brief
/// <c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>, decisions 3 and
/// 4). The two attributes are deliberately distinct types in distinct namespaces because this package
/// does not reference <c>Affiant.AgentFramework</c>; the resolution semantics below are identical, and
/// a parity test pins that. A tool type intended for both backends carries both attributes.
/// </para>
///
/// <para>
/// Microsoft.Extensions.AI ships its own <c>[AIFunctionName]</c> with the same purpose, but it is
/// marked <c>[Experimental]</c> as of Microsoft.Extensions.AI 10.9.0 (seam probe
/// <c>research/meai-seam-probe.md</c> §2), so Affiant keeps its own stable attribute rather than
/// take a dependency on an experimental API in a package that is about to hit beta.
/// </para>
///
/// Fills the same gap tracked as
/// <see href="https://github.com/Sakwala/affiant/issues/16">affiant#16</see>: Semantic Kernel's
/// <c>[KernelFunction("name")]</c> already lets a host declare
/// <c>[KernelFunction(ToolNames.SearchThing)]</c> — the attribute site, the prompt-visible name,
/// and every other reference (telemetry, extractor matching, provenance tags) share one
/// <c>ToolNames</c> constant symbol. Reflection-built <see cref="Microsoft.Extensions.AI.AIFunction"/>
/// catalogs had no equivalent, which forced a host in the framework's first MAF migration wave to
/// rename its C# methods themselves to snake_case just to get a deliberate, constant-backed tool
/// name — a workaround, not the standard. This attribute closes that gap: apply it with a
/// <c>ToolNames</c> constant and keep a conventional PascalCase C# method name.
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
/// A method with no <see cref="AffiantToolNameAttribute"/> is unaffected: its tool name is whatever
/// <c>AIFunctionFactory.Create</c> derives from the method (see
/// <see cref="AffiantToolCatalog.FromType{T}"/>'s remarks on the trailing-<c>Async</c> strip).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AffiantToolNameAttribute(string name) : Attribute
{
    /// <summary>The LLM-visible tool name to use in place of the method's C# name.</summary>
    public string Name { get; } = name;
}
