namespace QuickstartHost.Tests;

using Affiant.EntityFramework;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuickstartHost.Data;

/// <summary>
/// The sample host, in memory, on databases of its own.
///
/// <para>
/// The host's connection strings are relative file paths, which is right for a sample someone runs
/// by hand and wrong for a test: whichever directory the test runner happens to be in would collect
/// the files, and the next run would inherit them. <c>EnsureCreated</c> does not migrate an
/// existing file, so a database left behind by an older build of the schema fails the run with a
/// missing column rather than being rebuilt. This factory points both contexts at a fresh
/// directory per instance and deletes it afterwards.
/// </para>
/// </summary>
public sealed class QuickstartHostFactory(string environment, bool seamEnabled)
    : WebApplicationFactory<Program>
{
    private readonly string _databaseDirectory = Path.Combine(
        Path.GetTempPath(), $"affiant-quickstart-tests-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_databaseDirectory);

        builder.UseEnvironment(environment);
        builder.UseSetting("DevSeam:Enabled", seamEnabled ? "true" : "false");

        builder.ConfigureServices(services =>
        {
            // AddDbContext registers its options with TryAdd, so a second call is a no-op — the
            // existing registrations have to go before the replacements can take.
            services.RemoveAll<DbContextOptions<AffiantDbContext>>();
            services.RemoveAll<DbContextOptions<HrDbContext>>();
            services.RemoveAll<DbContextOptions>();

            services.AddDbContext<AffiantDbContext>(o => o.UseSqlite(ConnectionString("affiant")));
            services.AddDbContext<HrDbContext>(o => o.UseSqlite(ConnectionString("hr")));
        });
    }

    private string ConnectionString(string name) =>
        $"Data Source={Path.Combine(_databaseDirectory, $"{name}.db")}";

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        try
        {
            Directory.Delete(_databaseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A file handle the SQLite pool has not released yet. The directory is under the
            // system temp path; leaving it is not worth failing a test over.
        }
    }
}
