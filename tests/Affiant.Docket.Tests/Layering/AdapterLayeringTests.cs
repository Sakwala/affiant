using System.Reflection;
using Affiant.Docket.Stores;
using Affiant.EntityFramework.Stores;
using Xunit;

namespace Affiant.Docket.Tests.Layering;

/// <summary>
/// Gate-block guard for the adapter half of the framework layering DAG (invariant R1, stated in
/// <c>CLAUDE.md</c> under "Layering invariant"): the four adapter packages —
/// <c>Affiant.Docket</c>, <c>Affiant.EntityFramework</c>, <c>Affiant.Policies</c> and
/// <c>Affiant.Transport.SignalR</c> — may depend on <c>Affiant.Abstractions</c> and
/// <c>Affiant.Core</c> and on nothing else beginning with <c>Affiant.</c>. No adapter may reference
/// a sibling adapter.
///
/// Why this file exists: that invariant was violated for months by
/// <c>Affiant.Docket → Affiant.EntityFramework</c> (Sakwala/affiant#35), which existed solely so the
/// two SQL-backed <c>IDocketStore</c> implementations could take <c>AffiantDbContext</c>. Area-8
/// ruling 1 (2026-08-20) closed it by moving those two classes into <c>Affiant.EntityFramework</c>.
/// Nothing automated had been guarding the invariant — <c>Affiant.Core.Tests</c>'s
/// <c>LayeringStaticAnalysisTests</c> covers only the Abstractions and Core layers — so the
/// violation could return silently. This is that missing guard, placed here because
/// <c>Affiant.Docket.Tests</c> is the one test project that already references both sides of the
/// edge that broke (for the three-backend <c>IDocketStore</c> parity suite).
///
/// Scope note: this inspects the *emitted* assembly reference table, so it catches an adapter that
/// actually consumes a sibling adapter's types. A <c>ProjectReference</c> added but never used is
/// elided by the compiler and would slip past here — <c>EnablePackageValidation</c> plus the nuspec
/// dependency group is the check that catches that case at pack time.
/// </summary>
public class AdapterLayeringTests
{
    private static readonly string[] AllowedAffiantDependencies =
    [
        "Affiant.Abstractions",
        "Affiant.Core",
    ];

    public static TheoryData<string, Type> AdapterAnchors() => new()
    {
        { "Affiant.Docket", typeof(InMemoryDocketStore) },
        { "Affiant.EntityFramework", typeof(PostgresDocketStore) },
    };

    [Theory]
    [MemberData(nameof(AdapterAnchors))]
    public void Adapter_package_references_no_sibling_adapter(string packageName, Type anchor)
    {
        Assert.Equal(packageName, anchor.Assembly.GetName().Name);

        var illegal = anchor.Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("Affiant.", StringComparison.Ordinal))
            .Where(n => !AllowedAffiantDependencies.Contains(n, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(illegal);
    }

    /// <summary>
    /// The specific edge Sakwala/affiant#35 was filed against, asserted by name so a regression
    /// reads as "the affiant#35 violation is back" rather than as a generic layering failure.
    /// </summary>
    [Fact]
    public void AffiantDocket_does_not_reference_AffiantEntityFramework()
    {
        var referenced = typeof(InMemoryDocketStore).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        Assert.DoesNotContain("Affiant.EntityFramework", referenced);
    }
}
