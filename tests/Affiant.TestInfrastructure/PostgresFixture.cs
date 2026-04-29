namespace Affiant.TestInfrastructure;

using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// xUnit collection fixture that manages a throwaway Postgres container lifecycle.
/// Shared across every test class decorated with <c>[Collection("Postgres")]</c>
/// so container startup (~2s) is amortized across all Postgres-dependent tests.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>
    /// Database name for the test container. Defaults to "affiant_test";
    /// host apps can override via object initializer (e.g. <c>new PostgresFixture { Database = "meridian_test" }</c>).
    /// </summary>
    public string Database { get; init; } = "affiant_test";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase(Database)
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
