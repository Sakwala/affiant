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

    /// <summary>
    /// Registers Affiant descriptors for all [KernelFunction] methods in a single plugin type,
    /// defaulting the plugin name to <c>typeof(T).Name</c> — matching SK's own convention for
    /// <c>kernelBuilder.Plugins.AddFromType&lt;T&gt;()</c>.
    /// </summary>
    /// <remarks>
    /// Sibling overload of <see cref="AddAffiantPluginsFromAssembly"/>, scoped to one type.
    /// Useful when an assembly contains multiple plugin classes each registered under a distinct
    /// SK plugin name — call once per type rather than once per assembly.
    ///
    /// Write tools are identified by the presence of <c>[AffiantWriteTool]</c>; all other
    /// [KernelFunction] methods are classified as read tools (Operation.ReadQuery, EntityType: null).
    /// Methods without [KernelFunction] are silently skipped.
    /// </remarks>
    /// <param name="builder">The kernel builder.</param>
    /// <param name="pluginName">
    /// Plugin name applied to all descriptors. Defaults to <c>typeof(T).Name</c> when null.
    /// </param>
    /// <returns>The kernel builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <see cref="IAffiantToolRegistry"/> is not found — call services.AddAffiantCore() first.
    /// </exception>
    public static IKernelBuilder AddAffiantPluginsFromType<T>(
        this IKernelBuilder builder,
        string? pluginName = null) where T : class
    {
        pluginName ??= typeof(T).Name;

        var registryDescriptor = builder.Services.FirstOrDefault(
            d => d.ServiceType == typeof(IAffiantToolRegistry))
            ?? throw new InvalidOperationException(
                "IAffiantToolRegistry is not registered in IKernelBuilder.Services. " +
                "Call services.AddAffiantCore() before kernelBuilder.AddAffiantPluginsFromType().");

        var registry = ResolveOrCreateRegistry(builder.Services, registryDescriptor);

        foreach (var method in typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance))
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

        return builder;
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
