namespace QuickstartHost.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// One leave request in the sample's own system of record. Only an approved
/// <c>Affidavit</c> ever reaches this table, and only through
/// <see cref="Execution.LeaveWriteExecutor"/>.
/// </summary>
public sealed class LeaveRequest
{
    public int Id { get; set; }
    public string Employee { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string LeaveType { get; set; } = string.Empty;
    public int Days { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "Submitted";
}

/// <summary>
/// One employee, used only as the source for the reviewer's employee picker
/// (<c>GET /api/employees</c>). A picker fed from a real read endpoint is the point: the
/// reviewer amends a field from live data rather than retyping a name.
/// </summary>
public sealed class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
}

/// <summary>
/// The sample's domain database — deliberately separate from Affiant's own
/// <c>AffiantDbContext</c>, which stores chat sessions (and, on a SQL-backed Docket, review
/// entries). Domain data and framework data never share a context.
/// </summary>
public sealed class HrDbContext(DbContextOptions<HrDbContext> options) : DbContext(options)
{
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();

    public DbSet<Employee> Employees => Set<Employee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LeaveRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Employee).HasMaxLength(200).IsRequired();
            entity.Property(e => e.LeaveType).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(50).IsRequired();
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Department).HasMaxLength(200).IsRequired();
        });
    }

    /// <summary>
    /// Creates the schema if it is absent and seeds the employee list the reviewer's picker reads.
    /// Idempotent: safe to call on every start, and safe against a database file left behind by a
    /// previous run.
    /// </summary>
    public static async Task SeedAsync(HrDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (await db.Employees.AnyAsync(cancellationToken))
            return;

        db.Employees.AddRange(
            new Employee { Name = "Amara Silva", Department = "Engineering" },
            new Employee { Name = "Devon Park", Department = "Engineering" },
            new Employee { Name = "Ines Moreau", Department = "Finance" },
            new Employee { Name = "Kofi Mensah", Department = "Support" });

        await db.SaveChangesAsync(cancellationToken);
    }
}
