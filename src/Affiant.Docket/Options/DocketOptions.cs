namespace Affiant.Docket.Options;

public sealed class DocketOptions
{
    internal string? ConnectionString { get; private set; }
    internal bool UsePostgresProvider { get; private set; }
    internal bool UseSqliteProvider { get; private set; }
    internal bool UseInMemoryProvider { get; private set; }

    public void UsePostgres(string connectionString)
    {
        if (UseSqliteProvider || UseInMemoryProvider)
            throw new InvalidOperationException("Cannot combine Docket provider options.");
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        UsePostgresProvider = true;
    }

    public void UseSqlite(string connectionString)
    {
        if (UsePostgresProvider || UseInMemoryProvider)
            throw new InvalidOperationException("Cannot combine Docket provider options.");
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        UseSqliteProvider = true;
    }

    public void UseInMemory()
    {
        if (UsePostgresProvider || UseSqliteProvider)
            throw new InvalidOperationException("Cannot combine Docket provider options.");
        UseInMemoryProvider = true;
    }
}
