namespace Affiant.Core.Tests.Triggers;

using Affiant.Abstractions.Models;
using Affiant.Core.Services;
using Affiant.Core.Triggers;
using Xunit;

#pragma warning disable CS0618 // Testing the soft-deprecated fallback (PRD §2.5 / §10.3)

public class FunctionNameInferenceTriggerTests
{
    private static InferenceTriggerContext Ctx(string functionName, InferencePhase phase)
    {
        var fabric = new ContextFabric();
        return new InferenceTriggerContext(
            functionName,
            null,
            new Dictionary<string, object?>(),
            fabric,
            phase);
    }

    // --- Test 1: function in set + PreTool → true ---

    [Fact]
    public void FunctionInSet_PreTool_ReturnsTrue()
    {
        var trigger = new FunctionNameInferenceTrigger(["CreateThing"]);
        Assert.True(trigger.ShouldRun(Ctx("CreateThing", InferencePhase.PreTool)));
    }

    // --- Test 2: function not in set → false ---

    [Fact]
    public void FunctionNotInSet_ReturnsFalse()
    {
        var trigger = new FunctionNameInferenceTrigger(["CreateThing"]);
        Assert.False(trigger.ShouldRun(Ctx("UpdateThing", InferencePhase.PreTool)));
    }

    // --- Test 3: case-sensitive matching (Ordinal) ---

    [Fact]
    public void FunctionName_CaseSensitive_ReturnsFalse()
    {
        var trigger = new FunctionNameInferenceTrigger(["creatething"]);
        Assert.False(trigger.ShouldRun(Ctx("CreateThing", InferencePhase.PreTool)));
    }

    // --- Test 4: PostTool always returns false ---

    [Fact]
    public void FunctionInSet_PostTool_ReturnsFalse()
    {
        var trigger = new FunctionNameInferenceTrigger(["CreateThing"]);
        Assert.False(trigger.ShouldRun(Ctx("CreateThing", InferencePhase.PostTool)));
    }

    // --- Test 5: empty set always returns false ---

    [Fact]
    public void EmptySet_ReturnsFalse()
    {
        var trigger = new FunctionNameInferenceTrigger([]);
        Assert.False(trigger.ShouldRun(Ctx("CreateThing", InferencePhase.PreTool)));
    }

    // --- Test 6: multiple names, matching one ---

    [Fact]
    public void MultipleNames_MatchingOne_ReturnsTrue()
    {
        var trigger = new FunctionNameInferenceTrigger(["CreateThing", "UpdateThing"]);
        Assert.True(trigger.ShouldRun(Ctx("UpdateThing", InferencePhase.PreTool)));
    }
}

#pragma warning restore CS0618
