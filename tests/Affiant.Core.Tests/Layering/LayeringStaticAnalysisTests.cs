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
}
