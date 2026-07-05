namespace Affiant.Abstractions.Tests.Interfaces;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Xunit;

public class InferenceCompletionRequestTests
{
    // History is a backend-neutral IReadOnlyList<AffiantChatMessage>. Tests assert the structural
    // contract and reference equality only.

    private static InferenceCompletionRequest SampleRequest()
    {
        IReadOnlyList<AffiantChatMessage> history = new List<AffiantChatMessage>();
        var strategy = new StubStrategy();
        var args = new Dictionary<string, object?> { ["key"] = "value" };
        return new InferenceCompletionRequest(history, strategy, "TestFunction", args);
    }

    [Fact]
    public void Record_HasFourPositionalParameters()
    {
        var props = typeof(InferenceCompletionRequest).GetProperties();
        Assert.Equal(4, props.Length);
        Assert.Contains(props, p => p.Name == "History");
        Assert.Contains(props, p => p.Name == "Strategy");
        Assert.Contains(props, p => p.Name == "FunctionName");
        Assert.Contains(props, p => p.Name == "Arguments");
    }

    [Fact]
    public void History_PropertyType_IsNeutralMessageList()
    {
        var prop = typeof(InferenceCompletionRequest).GetProperty("History");
        Assert.NotNull(prop);
        Assert.Equal(typeof(IReadOnlyList<AffiantChatMessage>), prop.PropertyType);
    }

    [Fact]
    public void Strategy_PropertyType_IsITaskInferenceStrategy()
    {
        var prop = typeof(InferenceCompletionRequest).GetProperty("Strategy");
        Assert.NotNull(prop);
        Assert.Equal(typeof(ITaskInferenceStrategy), prop.PropertyType);
    }

    [Fact]
    public void NonDestructiveMutation_PreservesHistoryReference()
    {
        var original = SampleRequest();
        var mutated = original with { FunctionName = "OtherFunction" };
        Assert.Same(original.History, mutated.History);
    }

    [Fact]
    public void RecordEquality_HoldsWhenHistoryAndStrategyReferenceEqual()
    {
        IReadOnlyList<AffiantChatMessage> history = new List<AffiantChatMessage>();
        var strategy = new StubStrategy();
        var args = new Dictionary<string, object?>();
        var a = new InferenceCompletionRequest(history, strategy, "Fn", args);
        var b = new InferenceCompletionRequest(history, strategy, "Fn", args);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_FailsWhenFunctionNameDiffers()
    {
        var original = SampleRequest();
        var other = original with { FunctionName = "DifferentFunction" };
        Assert.NotEqual(original, other);
    }

    private sealed class StubStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "TestEntity";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }
}
