namespace Affiant.Transport.SignalR;

/// <summary>
/// Configuration options for the Affiant SignalR transport.
/// </summary>
public sealed class SignalROptions
{
    /// <summary>The hub endpoint path. Default: "/hubs/affiant".</summary>
    public string HubEndpoint { get; set; } = "/hubs/affiant";

    /// <summary>Maximum message size in bytes. Default: 32KB.</summary>
    public int MaximumMessageSize { get; set; } = 32768;

    /// <summary>Enable detailed error messages sent to clients. Default: false.</summary>
    public bool EnableDetailedErrors { get; set; } = false;
}
