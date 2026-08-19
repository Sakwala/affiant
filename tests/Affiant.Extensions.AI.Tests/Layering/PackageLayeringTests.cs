namespace Affiant.Extensions.AI.Tests.Layering;

using Xunit;

/// <summary>
/// Layering guard for <c>Affiant.Extensions.AI</c> — the assertions behind acceptance criterion 5 of
/// the M.E.AI adapter brief
/// (<c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>): no
/// <c>Microsoft.Agents.AI</c> and no provider client anywhere in the shipped package, and no sibling
/// Affiant adapter either.
///
/// <para>
/// Two invariants are at stake and they are not the same one. <b>Provider neutrality</b> is what the
/// package is for: it bridges Affiant to the Microsoft.Extensions.AI abstraction, so which concrete
/// client a host brings — OpenAI, Azure, Gemini, a local model — is the host's business and never
/// appears here. <b>Adapter layering</b> is invariant R1 (CLAUDE.md, "Layering invariant"), which
/// Area-8 re-established after <c>Affiant.Docket → Affiant.EntityFramework</c> (Sakwala/affiant#35)
/// had violated it undetected for months: an adapter may depend on <c>Affiant.Abstractions</c> and
/// <c>Affiant.Core</c> and on nothing else beginning with <c>Affiant.</c>.
/// </para>
///
/// <para>
/// The layering half is why this package's <c>AffiantToolCatalog</c>,
/// <c>ExtensionsAIInferenceCompletionPort</c> and <c>[AffiantToolName]</c> are copies of the
/// Microsoft Agent Framework adapter's files rather than references to them (decision 3). The
/// consolidation that would make a reference legal — inverting <c>Affiant.AgentFramework</c> onto
/// this package, since MAF sits on Microsoft.Extensions.AI — is deliberately deferred past beta, and
/// until then this test is what stops the reference from being added back as a convenience.
/// </para>
///
/// <para>
/// Scope note, the same one <c>Affiant.Docket.Tests.Layering.AdapterLayeringTests</c> carries: this
/// inspects the <em>emitted</em> assembly reference table, so it catches a dependency whose types are
/// actually consumed. A <c>PackageReference</c>/<c>ProjectReference</c> added but never used is
/// elided by the compiler and would slip past here — <c>EnablePackageValidation</c> and the nuspec
/// dependency group are what catch that case, at pack time.
/// </para>
/// </summary>
public class PackageLayeringTests
{
    private static readonly string[] AllowedAffiantDependencies =
    [
        "Affiant.Abstractions",
        "Affiant.Core",
    ];

    /// <summary>
    /// Assembly-name prefixes that must never appear: the Microsoft Agent Framework (this package
    /// exists precisely to be the M.E.AI-level bridge that does not need it), Semantic Kernel, and
    /// the concrete provider clients a provider-neutral package must not name.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.Agents.AI",
        "Microsoft.SemanticKernel",
        "Microsoft.Extensions.AI.OpenAI",
        "Microsoft.Extensions.AI.AzureAIInference",
        "Microsoft.Extensions.AI.Ollama",
        "Azure.AI",
        "OpenAI",
        "Google.GenAI",
        "Mscc.GenerativeAI",
        "Anthropic",
    ];

    [Fact]
    public void Package_references_no_sibling_Affiant_adapter()
    {
        var illegal = Anchor()
            .Where(n => n.StartsWith("Affiant.", StringComparison.Ordinal))
            .Where(n => !AllowedAffiantDependencies.Contains(n, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(illegal);
    }

    /// <summary>
    /// Stated as its own named test so a regression reads as "the MAF dependency is back" rather than
    /// as a generic layering failure — the whole premise of the package is that this reference is
    /// unnecessary.
    /// </summary>
    [Fact]
    public void Package_does_not_reference_MicrosoftAgentsAI()
    {
        Assert.DoesNotContain(Anchor(), n => n.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_references_no_backend_or_provider_client()
    {
        var referenced = Anchor();

        foreach (var forbidden in ForbiddenPrefixes)
            Assert.DoesNotContain(referenced, n => n.StartsWith(forbidden, StringComparison.Ordinal));
    }

    /// <summary>
    /// The positive half: the package really is built on the Microsoft.Extensions.AI abstraction, so
    /// an "empty forbidden list" result cannot be an artefact of the anchor resolving to the wrong
    /// assembly.
    /// </summary>
    [Fact]
    public void Package_is_built_on_MicrosoftExtensionsAI_abstractions()
    {
        Assert.Equal("Affiant.Extensions.AI", typeof(AffiantToolCatalog).Assembly.GetName().Name);
        Assert.Contains(Anchor(), n =>
            n.StartsWith("Microsoft.Extensions.AI", StringComparison.Ordinal));
    }

    private static List<string> Anchor() =>
        typeof(AffiantToolCatalog).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
}
