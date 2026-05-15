using System.Text;
using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;

namespace Affiant.SemanticKernel.Validation;

// IServiceScopeFactory is injected (not Kernel) so that the singleton IHostedService
// never forces Kernel resolution from the root scope. Hosts that register plugins with
// scoped dependencies (e.g. IDocketStore) would otherwise trip ValidateScopes=true
// when the root provider tries to resolve those scoped services during kernel construction.
public sealed class AffiantStartupValidator(
    IServiceScopeFactory scopeFactory,
    IAffiantToolRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();
        RunCheckA(kernel);
        RunCheckB(scope.ServiceProvider);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RunCheckA(Kernel kernel)
    {
        var unregistered = new List<(string Plugin, string Function)>();
        foreach (var plugin in kernel.Plugins)
        {
            foreach (var fn in plugin.GetFunctionsMetadata())
            {
                if (registry.Find(fn.Name, plugin.Name) is null)
                    unregistered.Add((plugin.Name, fn.Name));
            }
        }
        if (unregistered.Count == 0) return;

        var msg = new StringBuilder();
        msg.AppendLine("The following [KernelFunction] methods are not registered as Affiant tool descriptors:");
        foreach (var (plugin, function) in unregistered)
            msg.AppendLine($"- {plugin}.{function}");
        msg.AppendLine();
        msg.AppendLine(
            "Fix: apply [AffiantWriteTool(operation, entityType, typeof(TStrategy))] to the method, " +
            "or call services.AddAffiantTool<TStrategy>(\"FunctionName\", Operation.WriteCreate, \"EntityType\") " +
            "during DI setup. For read tools, use services.AddAffiantReadTool(\"FunctionName\").");

        throw new AffiantStartupException(msg.ToString());
    }

    private void RunCheckB(IServiceProvider serviceProvider)
    {
        var unresolvable = new List<(string Function, string StrategyFullName)>();
        foreach (var descriptor in registry.All)
        {
            if (descriptor.InferenceStrategy is null) continue;
            if (serviceProvider.GetService(descriptor.InferenceStrategy) is null)
                unresolvable.Add((
                    descriptor.FunctionName,
                    descriptor.InferenceStrategy.FullName ?? descriptor.InferenceStrategy.Name));
        }
        if (unresolvable.Count == 0) return;

        var msg = new StringBuilder();
        msg.AppendLine("The following Affiant tool descriptors name an inference strategy that cannot be resolved from IServiceProvider:");
        foreach (var (function, strategy) in unresolvable)
            msg.AppendLine($"- {function} → {strategy}");
        msg.AppendLine();
        msg.AppendLine(
            "Fix: register the strategy via services.AddSingleton<TStrategy>(), " +
            "or use AddAffiantTool<TStrategy>(...) which registers automatically.");

        throw new AffiantStartupException(msg.ToString());
    }
}
