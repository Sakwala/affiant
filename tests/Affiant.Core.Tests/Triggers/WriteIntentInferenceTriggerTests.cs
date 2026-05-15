namespace Affiant.Core.Tests.Triggers;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.Core.Triggers;
using Xunit;

public class WriteIntentInferenceTriggerTests
{
    private static AffiantToolRegistry BuildRegistry(params AffiantToolDescriptor[] descriptors)
    {
        var registry = new AffiantToolRegistry();
        foreach (var d in descriptors)
            registry.Register(d);
        return registry;
    }

    private static InferenceTriggerContext Ctx(
        string functionName,
        string? pluginName,
        InferencePhase phase,
        IAffiantToolRegistry? registry = null)
    {
        var fabric = new ContextFabric();
        return new InferenceTriggerContext(
            functionName,
            pluginName,
            new Dictionary<string, object?>(),
            fabric,
            phase);
    }

    // --- Test 1: WriteCreate + PreTool → true ---

    [Fact]
    public void WriteCreate_PreTool_ReturnsTrue()
    {
        var registry = BuildRegistry(new AffiantToolDescriptor(
            "CreateThing", null, Operation.WriteCreate, "Thing", null));
        var trigger = new WriteIntentInferenceTrigger(registry);

        Assert.True(trigger.ShouldRun(Ctx("CreateThing", null, InferencePhase.PreTool)));
    }

    // --- Test 2: WriteUpdate + PreTool → true ---

    [Fact]
    public void WriteUpdate_PreTool_ReturnsTrue()
    {
        var registry = BuildRegistry(new AffiantToolDescriptor(
            "UpdateThing", null, Operation.WriteUpdate, "Thing", null));
        var trigger = new WriteIntentInferenceTrigger(registry);

        Assert.True(trigger.ShouldRun(Ctx("UpdateThing", null, InferencePhase.PreTool)));
    }

    // --- Test 3: ReadQuery + PreTool → false ---

    [Fact]
    public void ReadQuery_PreTool_ReturnsFalse()
    {
        var registry = BuildRegistry(new AffiantToolDescriptor(
            "FindThings", null, Operation.ReadQuery, "Thing", null));
        var trigger = new WriteIntentInferenceTrigger(registry);

        Assert.False(trigger.ShouldRun(Ctx("FindThings", null, InferencePhase.PreTool)));
    }

    // --- Test 4: WriteCreate + PostTool → false ---

    [Fact]
    public void WriteCreate_PostTool_ReturnsFalse()
    {
        var registry = BuildRegistry(new AffiantToolDescriptor(
            "CreateThing", null, Operation.WriteCreate, "Thing", null));
        var trigger = new WriteIntentInferenceTrigger(registry);

        Assert.False(trigger.ShouldRun(Ctx("CreateThing", null, InferencePhase.PostTool)));
    }

    // --- Test 5: no descriptor found → false ---

    [Fact]
    public void NoDescriptor_ReturnsFalse()
    {
        var registry = BuildRegistry();
        var trigger = new WriteIntentInferenceTrigger(registry);

        Assert.False(trigger.ShouldRun(Ctx("UnknownFn", null, InferencePhase.PreTool)));
    }

    // --- Test 6: host-defined non-standard Kind → false ---

    [Fact]
    public void NonStandardKind_WriteUpsert_ReturnsFalse()
    {
        var registry = new AffiantToolRegistry();
        registry.Register(new AffiantToolDescriptor(
            "UpsertThing", null, new Operation("WriteUpsert"), "Thing", null));
        var trigger = new WriteIntentInferenceTrigger(registry);

        Assert.False(trigger.ShouldRun(Ctx("UpsertThing", null, InferencePhase.PreTool)));
    }

    // --- Test 7: PluginName-keyed descriptor resolves correctly ---

    [Fact]
    public void WriteCreate_WithPluginName_PreTool_ReturnsTrue()
    {
        var registry = BuildRegistry(new AffiantToolDescriptor(
            "CreateThing", "ThingPlugin", Operation.WriteCreate, "Thing", null));
        var trigger = new WriteIntentInferenceTrigger(registry);

        Assert.True(trigger.ShouldRun(Ctx("CreateThing", "ThingPlugin", InferencePhase.PreTool)));
    }

    // --- Test 8: constructor null guard ---

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WriteIntentInferenceTrigger(null!));
    }
}
