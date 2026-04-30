namespace Affiant.Transport.SignalR.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Transport.SignalR.Hubs;
using Affiant.Transport.SignalR.Transport;
using Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SignalRStreamingTransport{THub}"/> as the
    /// <see cref="IStreamingTransport"/> singleton and calls <c>AddSignalR()</c>.
    /// The host must still call <c>app.MapHub&lt;THub&gt;(path)</c>.
    /// </summary>
    public static IServiceCollection AddAffiantSignalR<THub>(this IServiceCollection services)
        where THub : AffiantHub
    {
        services.AddSignalR();
        services.AddSingleton<SignalRStreamingTransport<THub>>();
        services.AddSingleton<IStreamingTransport>(
            sp => sp.GetRequiredService<SignalRStreamingTransport<THub>>());
        return services;
    }
}
