namespace Affiant.Abstractions.Tests.Interfaces;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Xunit;

public class InferenceTriggerContextTests
{
    [Fact]
    public void Record_HasFivePositionalParameters()
    {
        var props = typeof(InferenceTriggerContext).GetProperties();
        Assert.Equal(5, props.Length);
        Assert.Contains(props, p => p.Name == "FunctionName");
        Assert.Contains(props, p => p.Name == "PluginName");
        Assert.Contains(props, p => p.Name == "Arguments");
        Assert.Contains(props, p => p.Name == "Fabric");
        Assert.Contains(props, p => p.Name == "Phase");
    }

    [Fact]
    public void Fabric_PropertyType_IsIContextFabric()
    {
        var prop = typeof(InferenceTriggerContext).GetProperty("Fabric");
        Assert.NotNull(prop);
        Assert.Equal(typeof(IContextFabric), prop.PropertyType);
    }

    [Fact]
    public void InferencePhase_HasExactlyTwoValues()
    {
        var values = Enum.GetValues<InferencePhase>();
        Assert.Equal(2, values.Length);
        Assert.Contains(InferencePhase.PreTool, values);
        Assert.Contains(InferencePhase.PostTool, values);
    }

    [Fact]
    public void InferencePhase_PreTool_IsZero()
    {
        Assert.Equal(0, (int)InferencePhase.PreTool);
    }

    [Fact]
    public void RecordEquality_HoldsForIdenticalValues()
    {
        var fabric = new StubFabric();
        var args = new Dictionary<string, object?>();
        var a = new InferenceTriggerContext("Fn", "Plugin", args, fabric, InferencePhase.PreTool);
        var b = new InferenceTriggerContext("Fn", "Plugin", args, fabric, InferencePhase.PreTool);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_FailsWhenPhaseDiffers()
    {
        var fabric = new StubFabric();
        var args = new Dictionary<string, object?>();
        var pre = new InferenceTriggerContext("Fn", null, args, fabric, InferencePhase.PreTool);
        var post = new InferenceTriggerContext("Fn", null, args, fabric, InferencePhase.PostTool);
        Assert.NotEqual(pre, post);
    }

    private sealed class StubFabric : IContextFabric
    {
        public ProvenanceChain? GetFieldChain(string fieldName) => null;
        public void SetFieldChain(string fieldName, ProvenanceChain chain) { }
        public EntityRef? GetByKey(string key) => null;
        public void Upsert(EntityRef entity) { }
    }
}
