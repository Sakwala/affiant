using Testcontainers.PostgreSql;

namespace Affiant.Docket.Tests.Fixtures;

/// <summary>
/// Static Postgres container shared across the entire test run.
/// Started once on first access; checks POSTGRES_CONNECTION_STRING env var first
/// so CI can inject a pre-existing service container instead of spinning up Docker.
/// </summary>
internal static class StaticPostgresContainer
{
    private static readonly Lazy<string> s_connectionString = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string GetConnectionString() => s_connectionString.Value;

    private static string Resolve()
    {
        var envCs = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envCs))
            return envCs;

        var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("affiant_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            container.StartAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Postgres testcontainer did not start within 60s. " +
                "Confirm Docker is running and the postgres:16-alpine image is pullable, " +
                "or set POSTGRES_CONNECTION_STRING to bypass the Testcontainers path.");
        }

        return container.GetConnectionString();
    }
}
