using System.Text.Json;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Serialization;
using Affiant.EntityFramework.Stores;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Affiant.EntityFramework.Tests;

/// <summary>
/// A Docket row's JSON columns are written under the framework's own conventions
/// (<see cref="AffiantJson"/>), not under a second set of options this package configured for
/// itself — so the bytes in the column are the bytes the same record has on the wire (SR-3, #105).
/// </summary>
/// <remarks>
/// The instant is what separates the two spellings: System.Text.Json's default writes
/// <c>2026-09-08T10:00:00+00:00</c> and <see cref="IsoInstantJsonConverter"/> writes
/// <c>2026-09-08T10:00:00.000Z</c>. Both name the same moment, so nothing was lost before this
/// fix; they are different bytes, which is what made a stored row and the wire form of the same
/// record impossible to compare.
/// </remarks>
public sealed class DocketRowJsonConventionsTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private AffiantDbContext _db = null!;
    private SqliteDocketStore _store = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AffiantDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AffiantDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        _store = new SqliteDocketStore(_db, NullLogger<SqliteDocketStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task StoredAffidavitJson_IsTheWireSerializationOfTheSameAffidavit()
    {
        var entry = EntryWithAStampedTag();

        await _store.FileDocketEntryAsync(entry, CancellationToken.None);

        var row = await _db.Docket
            .AsNoTracking()
            .SingleAsync(d => d.EntryId == entry.EntryId, CancellationToken.None);

        Assert.Equal(
            JsonSerializer.Serialize(entry.Envelope, AffiantJson.SerializerOptions),
            row.AffidavitJson);
        Assert.Contains("2026-09-08T10:00:00.000Z", row.AffidavitJson);
    }

    [Fact]
    public async Task StoredProvenanceChainsJson_IsTheWireSerializationOfTheSameChains()
    {
        var entry = EntryWithAStampedTag();

        await _store.FileDocketEntryAsync(entry, CancellationToken.None);

        var row = await _db.Docket
            .AsNoTracking()
            .SingleAsync(d => d.EntryId == entry.EntryId, CancellationToken.None);

        var chains = entry.Envelope.Fields.ToDictionary(f => f.Name, f => f.Provenance);

        Assert.Equal(
            JsonSerializer.Serialize(chains, AffiantJson.SerializerOptions),
            row.ProvenanceChainsJson);
    }

    /// <summary>
    /// An entry whose one field carries a tag stamped with an instant — the value the two option
    /// sets spell differently.
    /// </summary>
    private static DocketEntry EntryWithAStampedTag()
    {
        var at = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);

        var field = new AffidavitField(
            Name: "title",
            Value: "Quarterly review",
            PreviousValue: null,
            Provenance: ProvenanceChain.From(
                new ProvenanceTag(ProvenanceSource.Inferred, 0.9f, "LLM inferred: title", 1, null, at)));

        var affidavit = new Affidavit(
            OperationType: "test-op",
            EntityType: "test-entity",
            EntityId: "entity-001",
            Fields: [field],
            AggregateConfidence: 0.9f,
            PopulatedConfidence: 0.9f,
            EmptyFieldCount: 0,
            Warnings: [],
            RequiresConfirmation: false);

        return new DocketEntry(
            EntryId: Guid.NewGuid(),
            SessionId: "session-001",
            TenantId: "tenant-001",
            UserId: "user-001",
            ReviewerUserId: "reviewer-001",
            OperationType: "test-op",
            Envelope: affidavit,
            Status: ReviewStatus.Pending,
            CreatedAt: at,
            ExpiresAt: at.AddMinutes(10),
            Amendments: null);
    }
}
