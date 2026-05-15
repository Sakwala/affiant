namespace Affiant.SemanticKernel.Extensions;

using System.Reflection;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

public static class KernelBuilderExtensions
{
    public static IKernelBuilder AddAffiantPluginsFromAssembly(
        this IKernelBuilder builder,
        Assembly assembly,
        string? pluginName = null)
    {
        var registryDescriptor = builder.Services.FirstOrDefault(
            d => d.ServiceType == typeof(IAffiantToolRegistry))
            ?? throw new InvalidOperationException(
                "IAffiantToolRegistry is not registered in IKernelBuilder.Services. " +
                "Call services.AddAffiantCore() before kernelBuilder.AddAffiantPluginsFromAssembly().");

        var registry = ResolveOrCreateRegistry(builder.Services, registryDescriptor);

        foreach (var type in TryGetTypes(assembly))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.IsGenericMethodDefinition) continue;

                var kf = method.GetCustomAttribute<KernelFunctionAttribute>();
                if (kf is null) continue;

                var functionName = string.IsNullOrEmpty(kf.Name) ? method.Name : kf.Name;
                var write = method.GetCustomAttribute<AffiantWriteToolAttribute>();

                var descriptor = write is null
                    ? new AffiantToolDescriptor(functionName, pluginName, Operation.ReadQuery, null, null)
                    : new AffiantToolDescriptor(
                        functionName, pluginName,
                        new Operation(write.Operation),
                        write.EntityType,
                        write.InferenceStrategy);

                registry.Register(descriptor);
            }
        }

        return builder;
    }

    private static IAffiantToolRegistry ResolveOrCreateRegistry(
        IServiceCollection services,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IAffiantToolRegistry existing)
            return existing;

        if (descriptor.ImplementationType is not null)
        {
            var instance = Activator.CreateInstance(descriptor.ImplementationType)
                as IAffiantToolRegistry
                ?? throw new InvalidOperationException(
                    $"Activator.CreateInstance({descriptor.ImplementationType.Name}) did not produce an IAffiantToolRegistry.");

            // Pin instance so the built ServiceProvider returns the same singleton the walker filled.
            services.Remove(descriptor);
            services.AddSingleton<IAffiantToolRegistry>(instance);
            return instance;
        }

        throw new InvalidOperationException(
            "IAffiantToolRegistry is registered with a factory delegate, which is not supported by " +
            "AddAffiantPluginsFromAssembly. Use a type registration (TryAddSingleton<IAffiantToolRegistry, TImpl>()) " +
            "or an instance registration (AddSingleton<IAffiantToolRegistry>(instance)).");
    }

    // Best-effort — startup validator (15.5) is the load-bearing failure point, not the walker.
    private static IEnumerable<Type> TryGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}
