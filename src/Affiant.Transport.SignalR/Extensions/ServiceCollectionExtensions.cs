namespace Affiant.Transport.SignalR.Extensions;

using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <remarks>
    /// <b>P1d (area-4, ruled 2026-08-04): the hub JSON protocol is now declared explicitly, not
    /// inherited from ASP.NET Core's ambient defaults.</b> Before this change, <c>AddAffiantSignalR</c>
    /// never called <c>.AddJsonProtocol(...)</c> at all — camelCase property naming was real but
    /// accidental (inherited from <c>JsonHubProtocol</c>'s own default, <c>JsonSerializerDefaults.Web</c>)
    /// and asserted nowhere in framework source, only in a test comment. Enum treatment was
    /// inconsistent as a direct consequence: <see cref="Abstractions.Models.ProvenanceSource"/>
    /// carries an explicit <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c> attribute and
    /// crossed the wire as a string, while <see cref="Abstractions.Transport.ApprovalDecision"/> had
    /// no such attribute and crossed as a bare <c>System.Text.Json</c> integer — a latent
    /// inconsistency, visible only once a host's UI actually read <c>ApprovalDecision</c> off the
    /// wire. This method now configures the hub protocol explicitly: camelCase property naming
    /// (matching the previous accidental behavior, so no client-visible property-name change) plus a
    /// global <see cref="JsonStringEnumConverter"/> (resolving the <c>ApprovalDecision</c>
    /// inconsistency — it now crosses as a string, e.g. <c>"Approved"</c>/<c>"Rejected"</c>, matching
    /// <c>ProvenanceSource</c>'s treatment; see the CHANGELOG for this wire-visible change).
    /// <see cref="Abstractions.Models.AffidavitFieldKind"/> is unaffected — it was already a plain
    /// string constant, never a C# enum, by deliberate prior design (see its own doc comment).
    /// </remarks>
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
            })
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
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
