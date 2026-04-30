namespace Affiant.Transport.SignalR.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Transport.SignalR.Hubs;
using Affiant.Transport.SignalR.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SignalRStreamingTransport{THub}"/> as the
    /// <see cref="IStreamingTransport"/> singleton and calls <c>AddSignalR()</c>.
    /// The host must still call <c>app.MapHub&lt;THub&gt;(path)</c> or
    /// <c>app.MapAffiantSignalR&lt;THub&gt;()</c> in the pipeline.
    /// </summary>
    public static IServiceCollection AddAffiantSignalR<THub>(
        this IServiceCollection services,
        Action<SignalROptions>? configureOptions = null)
        where THub : AffiantHub
    {
        var options = new SignalROptions();
        configureOptions?.Invoke(options);

        services.AddSignalR(o =>
        {
            o.MaximumReceiveMessageSize = options.MaximumMessageSize;
            if (options.EnableDetailedErrors)
                o.EnableDetailedErrors = true;
        });

        services.AddSingleton<SignalRStreamingTransport<THub>>();
        services.AddSingleton<IStreamingTransport>(
            sp => sp.GetRequiredService<SignalRStreamingTransport<THub>>());

        return services;
    }

    /// <summary>
    /// Maps <typeparamref name="THub"/> to the endpoint configured in <see cref="SignalROptions"/>.
    /// Call this in <c>Program.cs</c> after routing middleware is configured.
    /// </summary>
    public static WebApplication MapAffiantSignalR<THub>(
        this WebApplication app,
        Action<SignalROptions>? configureOptions = null)
        where THub : AffiantHub
    {
        var options = new SignalROptions();
        configureOptions?.Invoke(options);
        app.MapHub<THub>(options.HubEndpoint);
        return app;
    }
}
