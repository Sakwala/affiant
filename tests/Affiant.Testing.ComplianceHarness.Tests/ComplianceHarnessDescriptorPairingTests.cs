namespace Affiant.Testing.ComplianceHarness.Tests;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// ---------------------------------------------------------------------------
// One strategy behind several write tools — the shape #107 reports.
// ---------------------------------------------------------------------------

internal sealed class FakePairingStrategy : ITaskInferenceStrategy
{
    public string EntityName => "Pairing";
    public IReadOnlyList<TaskInferenceField> Fields =>
    [
        new TaskInferenceField("Title", "string", "A title"),
    ];
    public double? MinimumConfidenceThreshold => null;
}

internal sealed class FakePairingFixture : ITaskInferenceComplianceFixture
{
    private readonly InferenceFixtureCase[] _cases;

    public FakePairingFixture(params InferenceFixtureCase[] cases) => _cases = cases;

    public Type Strategy => typeof(FakePairingStrategy);
    public IEnumerable<InferenceFixtureCase> Cases => _cases;
}

/// <summary>
/// <see cref="ComplianceHarness.Verify"/> pairs a fixture with a descriptor, and the registry it
/// reads them from is a <c>ConcurrentDictionary</c> whose enumeration order is unspecified and
/// varies between processes. Where one strategy backs two write tools, that made the pairing —
/// and so the report — a coin toss per run (#107). The pairing is now the first descriptor by
/// function name, then by plugin name, both ordinal.
/// </summary>
/// <remarks>
/// Each test below registers several write tools behind one strategy and asserts which one the
/// harness paired with. Before the fix each would pass only in the processes whose enumeration
/// order happened to put the ordinal-first descriptor first — one run in sixteen for the first
/// test, roughly one in two for the other two.
/// </remarks>
public class ComplianceHarnessDescriptorPairingTests
{
    private const string ValidTitleJson =
        """{"Title": {"value": "Test value", "confidence": 0.95}}""";

    private static IServiceCollection CreateBase()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddSingleton<IInferenceCompletionPort>(
            new FakeInferenceCompletionPort(ValidTitleJson));
        return services;
    }

    private static InferenceFixtureCase CreateShapedCase(string name) =>
        new(name, Array.Empty<AffiantChatMessage>(), new Dictionary<string, object?>(), _ => true);

    // The ordinal-first tool is the create-shaped one, and the case is create-shaped: pairing with
    // it is the only way this run passes. Fifteen update-shaped tools share the strategy, so a
    // pairing that follows the registry's own order passes one process in sixteen.
    [Fact]
    public void SharedStrategy_PairsWithTheOrdinalFirstFunctionName()
    {
        var services = CreateBase();
        services.AddAffiantTool<FakePairingStrategy>("tool_00", Operation.WriteCreate, "Pairing");
        for (var i = 1; i < 16; i++)
        {
            services.AddAffiantTool<FakePairingStrategy>(
                $"tool_{i:00}", Operation.WriteUpdate, "Pairing");
        }

        services.AddSingleton<ITaskInferenceComplianceFixture>(
            new FakePairingFixture(CreateShapedCase("create_shaped")));

        var result = ComplianceHarness.Verify(services);

        Assert.Empty(result.FixtureFailures);
        Assert.True(result.Passed);
    }

    // The rule cuts both ways, and this is the half that says *which* descriptor wins rather than
    // only that the run is stable: "amend_thing" sorts before "request_thing", so the update-shaped
    // tool is the pairing and a create-shaped case fails against it — in every process.
    [Fact]
    public void SharedStrategy_OrdinalFirstIsUpdateShaped_FailureNamesThatTool()
    {
        var services = CreateBase();
        services.AddAffiantTool<FakePairingStrategy>("amend_thing", Operation.WriteUpdate, "Pairing");
        services.AddAffiantTool<FakePairingStrategy>("request_thing", Operation.WriteCreate, "Pairing");

        services.AddSingleton<ITaskInferenceComplianceFixture>(
            new FakePairingFixture(CreateShapedCase("create_shaped")));

        var result = ComplianceHarness.Verify(services);

        var failure = Assert.Single(result.FixtureFailures);
        Assert.Equal("create_shaped", failure.FixtureCaseName);
        Assert.Contains("amend_thing", failure.Reason, StringComparison.Ordinal);
    }

    // Two descriptors can share a function name and differ only by plugin, which is the registry's
    // own key; the plugin name is the tiebreak, ordinal again.
    [Fact]
    public void SameFunctionName_PairsWithTheOrdinalFirstPluginName()
    {
        var services = CreateBase();
        services.AddAffiantTool<FakePairingStrategy>(
            "do_thing", Operation.WriteCreate, "Pairing", "PluginA");
        services.AddAffiantTool<FakePairingStrategy>(
            "do_thing", Operation.WriteUpdate, "Pairing", "PluginB");

        services.AddSingleton<ITaskInferenceComplianceFixture>(
            new FakePairingFixture(CreateShapedCase("create_shaped")));

        var result = ComplianceHarness.Verify(services);

        Assert.Empty(result.FixtureFailures);
        Assert.True(result.Passed);
    }
}
