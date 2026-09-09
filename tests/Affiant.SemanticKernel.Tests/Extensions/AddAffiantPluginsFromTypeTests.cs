namespace Affiant.SemanticKernel.Tests.Extensions;

using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.SemanticKernel.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Xunit;

public class AddAffiantPluginsFromTypeTests
{
    // ── Test fixtures ──────────────────────────────────────────────────────────

    private sealed class TestPluginReadOnly
    {
        [KernelFunction]
        public string ReadSomething() => "result";

        // Not decorated with [KernelFunction] — must be skipped by the walker.
        public string NotAPlugin() => "not registered";
    }

    private sealed class TestPluginWithAsyncMethods
    {
        [KernelFunction]
        public Task<string> SubmitExpenseReportAsync() => Task.FromResult("{}");

        [KernelFunction]
        public Task<string> SearchExpenseReportsAsync() => Task.FromResult("[]");

        [KernelFunction("explicit_name")]
        public Task<string> ExplicitNameAsync() => Task.FromResult("{}");
    }

    private sealed class TestPluginWithWrite
    {
        [KernelFunction]
        [AffiantWriteTool("WriteCreate", "TestEntity", typeof(FakeStrategy))]
        public string CreateSomething() => "created";

        [KernelFunction]
        public string GetContext() => "context";
    }

    // affiant#101: one method per shape SK's own default-naming fallback distinguishes. The
    // walker's descriptor name must equal the name SK gives the same method — asserted against
    // SK itself in SkWalkerNames_MatchSemanticKernelsOwnFunctionNames below, so the pin survives
    // a change in SK's convention rather than freezing this test's reading of it.
    private sealed class TestPluginWithAsyncSuffixShapes
    {
        // Synchronous: SK keeps the suffix.
        [KernelFunction]
        public string LookupThingAsync() => "{}";

        [KernelFunction]
        public Task<string> FetchThingAsync() => Task.FromResult("{}");

        [KernelFunction]
        public ValueTask<string> LoadThingAsync() => ValueTask.FromResult("{}");

        [KernelFunction]
        public Task SaveThingAsync() => Task.CompletedTask;

        [KernelFunction]
        public ValueTask BareValueTaskAsync() => ValueTask.CompletedTask;

        [KernelFunction]
        public async IAsyncEnumerable<string> StreamAsync()
        {
            await Task.Yield();
            yield return "{}";
        }

        // "Async" is the whole name: SK keeps it.
        [KernelFunction]
        public Task Async() => Task.CompletedTask;

        [KernelFunction]
        public string CountThings() => "1";
    }

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "TestEntity";
        public IReadOnlyList<TaskInferenceField> Fields => Array.Empty<TaskInferenceField>();
        public double? MinimumConfidenceThreshold => null;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IServiceProvider BuildServiceProvider<T>(string? pluginName = null) where T : class
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();

        var builder = Kernel.CreateBuilder();
        foreach (var sd in services)
            builder.Services.Add(sd);

        builder.AddAffiantPluginsFromType<T>(pluginName);

        return builder.Services.BuildServiceProvider();
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WalksTargetType_RegistersKernelFunctionMethods()
    {
        var sp = BuildServiceProvider<TestPluginReadOnly>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();
        var all = registry.All.ToList();

        // ReadSomething has [KernelFunction]; NotAPlugin does not — only ReadSomething registered.
        Assert.Single(all);
        Assert.Equal("ReadSomething", all[0].FunctionName);
        Assert.Equal("TestPluginReadOnly", all[0].PluginName);
    }

    [Fact]
    public void PluginName_DefaultsToTypeName()
    {
        var sp = BuildServiceProvider<TestPluginReadOnly>(pluginName: null);
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.Equal("TestPluginReadOnly", registry.All.Single().PluginName);
    }

    [Fact]
    public void PluginName_HonorsExplicitValue()
    {
        var sp = BuildServiceProvider<TestPluginReadOnly>(pluginName: "CustomName");
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.Equal("CustomName", registry.All.Single().PluginName);
    }

    [Fact]
    public void WriteAttribute_DetectedAndPreserved()
    {
        var sp = BuildServiceProvider<TestPluginWithWrite>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var descriptor = registry.Find("CreateSomething", "TestPluginWithWrite");

        Assert.NotNull(descriptor);
        Assert.Equal("WriteCreate", descriptor.Operation.Kind);
        Assert.Equal("TestEntity", descriptor.EntityType);
        Assert.Equal(typeof(FakeStrategy), descriptor.InferenceStrategy);
    }

    [Fact]
    public void ReadByAbsence_HasNullEntityAndStrategy()
    {
        var sp = BuildServiceProvider<TestPluginWithWrite>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var descriptor = registry.Find("GetContext", "TestPluginWithWrite");

        Assert.NotNull(descriptor);
        Assert.Equal(Operation.ReadQuery, descriptor.Operation);
        Assert.Null(descriptor.EntityType);
        Assert.Null(descriptor.InferenceStrategy);
    }

    [Fact]
    public void NonKernelFunctionMethods_AreSkipped()
    {
        var sp = BuildServiceProvider<TestPluginWithWrite>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();
        var all = registry.All.ToList();

        // TestPluginWithWrite has exactly 2 [KernelFunction] methods.
        Assert.Equal(2, all.Count);
        Assert.All(all, d => Assert.Equal("TestPluginWithWrite", d.PluginName));
    }

    [Fact]
    public void ThrowsOnMissingRegistry()
    {
        // Skip AddAffiantCore() so IAffiantToolRegistry is absent.
        var builder = Kernel.CreateBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            builder.AddAffiantPluginsFromType<TestPluginReadOnly>());

        Assert.Contains("AddAffiantCore", ex.Message);
    }

    [Fact]
    public void EmptyPluginType_RegistersNothing()
    {
        // string has no [KernelFunction] methods — walker must return cleanly with zero registrations.
        var sp = BuildServiceProvider<string>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.Empty(registry.All);
    }

    [Fact]
    public void StripsTrailingAsync_FromBareKernelFunctionFallback()
    {
        var sp = BuildServiceProvider<TestPluginWithAsyncMethods>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var submit = registry.Find("SubmitExpenseReport", "TestPluginWithAsyncMethods");
        var search = registry.Find("SearchExpenseReports", "TestPluginWithAsyncMethods");

        Assert.NotNull(submit);
        Assert.Equal("SubmitExpenseReport", submit.FunctionName);
        Assert.NotNull(search);
        Assert.Equal("SearchExpenseReports", search.FunctionName);
    }

    [Fact]
    public void DoesNotStrip_WhenExplicitKernelFunctionName()
    {
        // ExplicitNameAsync has [KernelFunction("explicit_name")] — the explicit name is used as-is.
        var sp = BuildServiceProvider<TestPluginWithAsyncMethods>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        var descriptor = registry.Find("explicit_name", "TestPluginWithAsyncMethods");

        Assert.NotNull(descriptor);
        Assert.Equal("explicit_name", descriptor.FunctionName);
    }

    [Fact]
    public void AsyncStrip_ProducesThreeDescriptors_ForThreeMethods()
    {
        var sp = BuildServiceProvider<TestPluginWithAsyncMethods>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.Equal(3, registry.All.Count());
    }

    /// <summary>
    /// affiant#101: a synchronous method whose name ends in "Async" keeps its name. SK's own
    /// default-naming fallback strips the suffix only from an async-returning method, so a walker
    /// that stripped it unconditionally registered <c>LookupThing</c> against an SK function named
    /// <c>LookupThingAsync</c> and <c>AffiantStartupValidator</c> refused the wiring at boot.
    /// </summary>
    [Fact]
    public void DoesNotStripAsync_FromSynchronousMethod()
    {
        var sp = BuildServiceProvider<TestPluginWithAsyncSuffixShapes>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.NotNull(registry.Find("LookupThingAsync", "TestPluginWithAsyncSuffixShapes"));
        Assert.Null(registry.Find("LookupThing", "TestPluginWithAsyncSuffixShapes"));
    }

    [Fact]
    public void StripsAsync_FromTask_ValueTask_AndBareTaskReturningMethods()
    {
        var sp = BuildServiceProvider<TestPluginWithAsyncSuffixShapes>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.NotNull(registry.Find("FetchThing", "TestPluginWithAsyncSuffixShapes"));
        Assert.NotNull(registry.Find("LoadThing", "TestPluginWithAsyncSuffixShapes"));
        Assert.NotNull(registry.Find("SaveThing", "TestPluginWithAsyncSuffixShapes"));
        Assert.NotNull(registry.Find("BareValueTask", "TestPluginWithAsyncSuffixShapes"));
        Assert.NotNull(registry.Find("Stream", "TestPluginWithAsyncSuffixShapes"));
    }

    /// <summary>
    /// affiant#101: SK keeps the name of a method called <c>Async</c> — the suffix is never the
    /// whole name — so the walker's length guard is what stops it registering an empty name.
    /// </summary>
    [Fact]
    public void DoesNotStripAsync_WhenAsyncIsTheWholeName()
    {
        var sp = BuildServiceProvider<TestPluginWithAsyncSuffixShapes>();
        var registry = sp.GetRequiredService<IAffiantToolRegistry>();

        Assert.NotNull(registry.Find("Async", "TestPluginWithAsyncSuffixShapes"));
    }

    /// <summary>
    /// The load-bearing assertion for affiant#101: the descriptor names the walker registers are
    /// the names Semantic Kernel itself gives the same methods. Compared against a real SK plugin
    /// built from the same type, so the trailing-<c>Async</c> rule cannot drift.
    /// </summary>
    [Fact]
    public void SkWalkerNames_MatchSemanticKernelsOwnFunctionNames()
    {
        var skNames = KernelPluginFactory
            .CreateFromObject(new TestPluginWithAsyncSuffixShapes(), nameof(TestPluginWithAsyncSuffixShapes))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var sp = BuildServiceProvider<TestPluginWithAsyncSuffixShapes>();
        var affiantNames = sp.GetRequiredService<IAffiantToolRegistry>().All
            .Select(d => d.FunctionName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(skNames, affiantNames);
    }
}
