namespace Affiant.SemanticKernel.Tests.Validation;

using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.SemanticKernel.Extensions;
using Affiant.SemanticKernel.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Xunit;

public class StartupValidatorTests
{
    // 1. Happy path: both checks pass
    [Fact]
    public async Task AllGood_StartsCleanly()
    {
        var kernel = BuildKernelWith(new SinglePlugin(), "FakePlugin");
        var validator = BuildValidator(kernel, servicesSetup: services =>
            services.AddAffiantTool<FakeStrategy>(
                "CreateValidator", Operation.WriteCreate, "Thing", "FakePlugin"));

        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    // 2. Check A: unregistered [KernelFunction] produces the right exception
    [Fact]
    public async Task UnregisteredKernelFunction_ProducesCheckAException()
    {
        var kernel = BuildKernelWith(new SinglePlugin(), "FakePlugin");
        var validator = BuildValidator(kernel); // no descriptor registered

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("FakePlugin", ex.Message);
        Assert.Contains("CreateValidator", ex.Message);
        Assert.Contains("are not registered as Affiant tool descriptors", ex.Message);
    }

    // 3. Check B: unresolvable strategy produces the right exception
    [Fact]
    public async Task UnresolvableStrategy_ProducesCheckBException()
    {
        var kernel = BuildKernelWith(new SinglePlugin(), "FakePlugin");
        // Descriptor registered directly — strategy NOT added to DI
        var validator = BuildValidator(kernel, registrySetup: registry =>
            registry.Register(new AffiantToolDescriptor(
                "CreateValidator", "FakePlugin", Operation.WriteCreate, "Thing", typeof(FakeStrategy))));

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(typeof(FakeStrategy).FullName!, ex.Message);
        Assert.Contains("CreateValidator", ex.Message);
        Assert.Contains("cannot be resolved from IServiceProvider", ex.Message);
    }

    // 4. Fix suggestions appear in both error messages
    [Fact]
    public async Task Messages_ContainFixSuggestions()
    {
        var kernel = BuildKernelWith(new SinglePlugin(), "FakePlugin");

        // Check A message
        var validatorA = BuildValidator(kernel);
        var exA = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validatorA.StartAsync(CancellationToken.None));
        Assert.Contains("AffiantWriteTool", exA.Message);
        Assert.Contains("AddAffiantTool<TStrategy>", exA.Message);

        // Check B message
        var validatorB = BuildValidator(kernel, registrySetup: registry =>
            registry.Register(new AffiantToolDescriptor(
                "CreateValidator", "FakePlugin", Operation.WriteCreate, "Thing", typeof(FakeStrategy))));
        var exB = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validatorB.StartAsync(CancellationToken.None));
        Assert.Contains("AddSingleton<TStrategy>", exB.Message);
    }

    // 5. Ordering contract: validator is the first IHostedService and stays first
    [Fact]
    public void OrderingTest_ValidatorIsFirstHostedService()
    {
        var services = new ServiceCollection();
        services.AddAffiantCore();
        services.AddAffiantSemanticKernel();

        var first = services.First(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(AffiantStartupValidator), first.ImplementationType);

        // Adding a host-registered background service must not displace the validator
        services.AddSingleton<IHostedService, FakeBackgroundWorker>();

        var stillFirst = services.First(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(AffiantStartupValidator), stillFirst.ImplementationType);
    }

    // 6. Multiple unregistered functions are all named in the Check A message
    [Fact]
    public async Task MultipleUnregisteredFunctions_AreAllNamedInMessage()
    {
        var kernel = BuildKernelWith(new ThreePlugin(), "FakePlugin");
        var validator = BuildValidator(kernel); // no descriptors

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("ValidatorCreate", ex.Message);
        Assert.Contains("ValidatorUpdate", ex.Message);
        Assert.Contains("ValidatorDelete", ex.Message);
    }

    // 7. Multiple unresolvable strategies are all named in the Check B message
    [Fact]
    public async Task MultipleUnresolvableStrategies_AreAllNamedInMessage()
    {
        var kernel = BuildKernelWith(new ThreePlugin(), "FakePlugin");
        var validator = BuildValidator(kernel, registrySetup: registry =>
        {
            registry.Register(new AffiantToolDescriptor("ValidatorCreate", "FakePlugin", Operation.WriteCreate, "Thing", typeof(FakeStrategy)));
            registry.Register(new AffiantToolDescriptor("ValidatorUpdate", "FakePlugin", Operation.WriteCreate, "Thing", typeof(FakeStrategy2)));
            registry.Register(new AffiantToolDescriptor("ValidatorDelete", "FakePlugin", Operation.WriteCreate, "Thing", typeof(FakeStrategy3)));
        });
        // None of the strategy types are registered in DI

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(typeof(FakeStrategy).FullName!, ex.Message);
        Assert.Contains(typeof(FakeStrategy2).FullName!, ex.Message);
        Assert.Contains(typeof(FakeStrategy3).FullName!, ex.Message);
    }

    // 8. Check A fires before Check B when both would fail
    [Fact]
    public async Task CheckA_RunsBeforeCheckB()
    {
        // CreateValidator is in kernel but not in registry → Check A catches it
        // UnrelatedFunction is in registry with an unresolvable strategy → Check B would catch it if reached
        var kernel = BuildKernelWith(new SinglePlugin(), "FakePlugin");
        var validator = BuildValidator(kernel, registrySetup: registry =>
            registry.Register(new AffiantToolDescriptor(
                "UnrelatedFunction", "OtherPlugin", Operation.WriteCreate, "Thing", typeof(FakeStrategy))));

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        // Must be the Check A message, not Check B
        Assert.Contains("are not registered as Affiant tool descriptors", ex.Message);
        Assert.DoesNotContain("cannot be resolved from IServiceProvider", ex.Message);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Kernel BuildKernelWith(object pluginInstance, string pluginName)
    {
        var builder = Kernel.CreateBuilder();
        builder.Plugins.AddFromObject(pluginInstance, pluginName);
        return builder.Build();
    }

    private static AffiantStartupValidator BuildValidator(
        Kernel kernel,
        Action<IServiceCollection>? servicesSetup = null,
        Action<IAffiantToolRegistry>? registrySetup = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore();
        services.AddAffiantSemanticKernel();
        servicesSetup?.Invoke(services);
        services.AddSingleton(kernel);
        var sp = services.BuildServiceProvider();
        registrySetup?.Invoke(sp.GetRequiredService<IAffiantToolRegistry>());
        return sp.GetServices<IHostedService>().OfType<AffiantStartupValidator>().First();
    }

    // ── Plugin stubs ─────────────────────────────────────────────────────────
    // Function names are prefixed "Validator*" / use unique identifiers to avoid
    // collisions when AddAffiantPluginsFromAssemblyTests scans the same test assembly.

    private sealed class SinglePlugin
    {
        [KernelFunction("CreateValidator")]
        public string CreateValidator(string name) => name;
    }

    private sealed class ThreePlugin
    {
        [KernelFunction("ValidatorCreate")]
        public string ValidatorCreate() => "";

        [KernelFunction("ValidatorUpdate")]
        public string ValidatorUpdate() => "";

        [KernelFunction("ValidatorDelete")]
        public string ValidatorDelete() => "";
    }

    private sealed class FakeBackgroundWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ── Strategy stubs ───────────────────────────────────────────────────────

    private sealed class FakeStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "ValidatorEntity";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class FakeStrategy2 : ITaskInferenceStrategy
    {
        public string EntityName => "ValidatorEntity2";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class FakeStrategy3 : ITaskInferenceStrategy
    {
        public string EntityName => "ValidatorEntity3";
        public IReadOnlyList<TaskInferenceField> Fields => [];
        public double? MinimumConfidenceThreshold => null;
    }
}
