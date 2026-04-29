namespace Affiant.TestInfrastructure.Telemetry;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

/// <summary>
/// Wires an OpenTelemetry in-memory trace exporter into a test host's service collection
/// so tests can assert on captured spans without emitting to a real backend.
/// </summary>
public sealed class InMemoryExporterHelper
{
    private readonly List<Activity> _activities = [];

    /// <summary>
    /// All activity spans collected since this helper was registered.
    /// </summary>
    public IReadOnlyList<Activity> ExportedActivities => _activities;

    /// <summary>
    /// Adds an in-memory activity exporter to <paramref name="services"/>.
    /// Call once from <c>WebApplicationFactory.ConfigureWebHost</c>.
    /// </summary>
    public void RegisterWithServices(IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddInMemoryExporter(_activities));
    }

    public IEnumerable<Activity> GetCapturedActivities() => _activities;
}
