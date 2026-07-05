namespace Affiant.Core.Tests.Layering;

using System.Reflection;
using Affiant.Core.Services;
using Xunit;

/// <summary>
/// Gate-block tests for the framework layering DAG (invariant R1).
/// These fail the build if the dependency graph is violated.
/// </summary>
public class LayeringStaticAnalysisTests
{
    [Fact]
    public void AffiantAbstractions_has_zero_Affiant_dependencies()
    {
        var abstractionsAssembly = typeof(Affiant.Abstractions.Models.ToolEnvelope).Assembly;
        var affiantRefs = abstractionsAssembly
            .GetReferencedAssemblies()
            .Where(a => a.Name != null && a.Name.StartsWith("Affiant.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(affiantRefs);
    }

    [Fact]
    public void AffiantCore_references_only_Abstractions_among_Affiant_packages()
    {
        var coreAssembly = typeof(ContextFabric).Assembly;
        var affiantRefs = coreAssembly
            .GetReferencedAssemblies()
            .Where(a => a.Name != null && a.Name.StartsWith("Affiant.", StringComparison.Ordinal))
            .ToList();

        var illegalRefs = affiantRefs
            .Where(a => a.Name != "Affiant.Abstractions")
            .ToList();

        Assert.Empty(illegalRefs);
        Assert.Contains(affiantRefs, a => a.Name == "Affiant.Abstractions");
    }

    // The interception pipeline in Abstractions + Core is backend-neutral (L2 AC #4). Neither
    // package may take a direct dependency on any agent-framework backend — not Semantic Kernel,
    // and not the Microsoft Agent Framework stack. This guard fails the build if one creeps back in.
    private static readonly string[] ForbiddenBackendAssemblies =
    [
        "Microsoft.SemanticKernel",
        "Microsoft.Agents.AI",
        "Microsoft.Extensions.AI",
    ];

    [Theory]
    [InlineData(typeof(Affiant.Abstractions.Models.ToolEnvelope))]
    [InlineData(typeof(ContextFabric))]
    public void AffiantNeutralPackages_reject_backend_framework_references(Type anchor)
    {
        var referenced = anchor.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .ToList();

        foreach (var forbidden in ForbiddenBackendAssemblies)
        {
            Assert.DoesNotContain(referenced, n =>
                n!.StartsWith(forbidden, StringComparison.Ordinal));
        }
    }
}
