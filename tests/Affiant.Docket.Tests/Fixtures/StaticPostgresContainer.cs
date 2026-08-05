using Affiant.EntityFramework;
using Microsoft.EntityFrameworkCore;
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

    // Lazy<T>'s own default thread-safety mode (ExecutionAndPublication) gives every caller —
    // regardless of which ClassData factory or test class reaches it first — exactly one
    // EnsureCreated run against the shared container, avoiding a second, independently-tracked
    // "did I already create the schema" flag per factory.
    private static readonly Lazy<bool> s_schemaCreated = new(() =>
    {
        var options = new DbContextOptionsBuilder<AffiantDbContext>().UseNpgsql(GetConnectionString()).Options;
        using var db = new AffiantDbContext(options);
        db.Database.EnsureCreated();
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string GetConnectionString() => s_connectionString.Value;

    /// <summary>Idempotently ensures the Affiant schema exists on the shared container. Safe to call from multiple ClassData factories.</summary>
    public static void EnsureSchemaCreated() => _ = s_schemaCreated.Value;

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
