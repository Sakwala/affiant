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

        container.StartAsync().GetAwaiter().GetResult();
        return container.GetConnectionString();
    }
}
