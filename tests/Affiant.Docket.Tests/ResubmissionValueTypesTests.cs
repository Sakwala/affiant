namespace Affiant.Docket.Tests;

using System.Globalization;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Transport;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.Docket.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

/// <summary>
/// A record that has been through a store proposes, scores and reads as the record that went in.
/// </summary>
/// <remarks>
/// <para>
/// The defect this closes: an Affidavit's field values are <c>object?</c>, so a store round trip
/// used to hand every one of them back as a raw JSON element rather than the number, string or
/// boolean the projection put there. A host risk scorer that pattern-matches on a value's type then
/// saw an unrecognised type for <em>every</em> field of every stored row and fell through to its
/// default grade — so identical content scored one way when first filed and another way when
/// resubmitted. Resubmission is the path that always reads the record back out, which is where it
/// showed.
/// </para>
/// <para>
/// Run against every backend the framework ships, because the bug lives at the serialization
/// boundary: the in-memory store keeps object references and never had it, and a test that only
/// covered that store would prove nothing about the two that did.
/// </para>
/// </remarks>
public class ResubmissionValueTypesTests
{
    [Theory]
    [ClassData(typeof(FakeClockDocketStoreProviderFactory))]
    public async Task AResubmittedProposal_CarriesTypedValues_NotRawJson(
        IDocketStore store, FakeTimeProvider clock, string providerName)
    {
        var gate = new ReviewGate(
            new SilentTransport(),
            store,
            new ReviewerConfirmationEvaluator(),
            new AffiantCoreOptions { DefaultDocketTtl = TimeSpan.FromMinutes(30) },
            NullLogger<ReviewGate>.Instance,
            clock,
            new AdmitEveryone());

        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var entryId = Guid.NewGuid();
        var sworn = Affidavit.Create(
            operationType: "CreateOrder",
            entityType: "Order",
            entityId: null,
            fields:
            [
                Field("title", "Widget", AffidavitFieldKind.Text),
                Field("quantity", 42, AffidavitFieldKind.Number),
                Field("unitPrice", 19.95m, AffidavitFieldKind.Number),
                Field("expedited", true, AffidavitFieldKind.Text),
            ],
            warnings: []);

        await gate.FileForReviewAsync(
            new WriteProposal("CreateOrder", clock.GetUtcNow(), sworn),
            new ReviewContext(
                SessionId: "conversation-1",
                TenantId: tenantId,
                UserId: "ana",
                ReviewerUserId: "ana",
                Affidavit: sworn,
                EntryId: entryId));

        // Let the window lapse, then correct one field too late — so the resubmission has both a
        // stored Affidavit and a stored amendment map to read back.
        clock.Advance(TimeSpan.FromHours(1));
        var context = new DecisionContext(new Principal.Member("ana"), tenantId);
        await gate.HandleDecisionAsync(
            entryId,
            ApprovalDecision.Approved,
            context,
            new Dictionary<string, object?> { ["quantity"] = 7 });

        var filing = await gate.ResubmitAsync(entryId, context);
        var fresh = Assert.IsType<ReviewFilingResult.RequiresReview>(filing);

        var resubmitted = await store.GetDocketEntryAsync(fresh.EntryId, default);
        Assert.NotNull(resubmitted);

        // Nothing a policy or a scorer sees is a JSON element…
        Assert.All(
            resubmitted!.Envelope.Fields,
            f => Assert.False(
                f.Value is JsonElement,
                $"{providerName}: field '{f.Name}' came back as raw JSON, so a scorer that matches " +
                "on its type would grade it differently from the first filing."));

        // …and every value still says what it said. A number's CLR width is the store's business —
        // the in-memory one keeps the reference it was handed, the SQL ones read the JSON back — so
        // the assertion is about the value and the fact that a scorer can read it as a number, not
        // about which integral type it landed in.
        Assert.Equal("Widget", Value(resubmitted.Envelope, "title"));
        Assert.Equal(42m, Number(resubmitted.Envelope, "quantity"));
        Assert.Equal(19.95m, Number(resubmitted.Envelope, "unitPrice"));
        Assert.Equal(true, Value(resubmitted.Envelope, "expedited"));

        // The reviewer's late correction rides along as a value, not as a JSON element either.
        Assert.NotNull(resubmitted.Amendments);
        Assert.False(resubmitted.Amendments!["quantity"] is JsonElement);
        Assert.Equal(7m, Convert.ToDecimal(resubmitted.Amendments["quantity"], CultureInfo.InvariantCulture));
    }

    [Theory]
    [ClassData(typeof(FakeClockDocketStoreProviderFactory))]
    public async Task AStoredRow_ReadsBackTyped_WhicheverWayItIsRead(
        IDocketStore store, FakeTimeProvider clock, string providerName)
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var entryId = Guid.NewGuid();
        var sworn = Affidavit.Create(
            operationType: "UpdateOrder",
            entityType: "Order",
            entityId: "order-1",
            fields: [Field("quantity", 42, AffidavitFieldKind.Number, previousValue: 3)],
            warnings: []);

        await store.FileDocketEntryAsync(
            new DocketEntry(
                EntryId: entryId,
                SessionId: "conversation-1",
                TenantId: tenantId,
                UserId: "ana",
                ReviewerUserId: "ana",
                OperationType: "UpdateOrder",
                Envelope: sworn,
                Status: ReviewStatus.Pending,
                CreatedAt: clock.GetUtcNow(),
                ExpiresAt: clock.GetUtcNow().AddMinutes(30),
                Amendments: null),
            default);

        var byId = await store.GetDocketEntryAsync(entryId, default);
        Assert.False(Value(byId!.Envelope, "quantity") is JsonElement, providerName);
        Assert.Equal(42m, Number(byId.Envelope, "quantity"));

        // A previous value is read back the same way a proposed one is — a reviewer comparing them
        // on a card is comparing two numbers, not a number and a JSON element.
        var previous = byId.Envelope.Fields.Single().PreviousValue;
        Assert.False(previous is JsonElement, providerName);
        Assert.Equal(3m, Convert.ToDecimal(previous, CultureInfo.InvariantCulture));

        var listed = await store.ListPendingAsync(
            new DocketScope(tenantId), new DocketPage(10, null), default);
        Assert.Equal(42m, Number(listed.Items.Single().Envelope, "quantity"));
    }

    private static AffidavitField Field(
        string name, object? value, string kind, object? previousValue = null) =>
        new(
            name,
            value,
            previousValue,
            ProvenanceChain.From(ProvenanceTag.FromInference(InferenceSource.Inferred, name, 0.9f)),
            IsMandatory: false,
            Kind: kind);

    private static object? Value(Affidavit affidavit, string name) =>
        affidavit.Fields.Single(f => f.Name == name).Value;

    /// <summary>The field's value as a number — which is the whole point: a scorer can read it.</summary>
    private static decimal Number(Affidavit affidavit, string name) =>
        Convert.ToDecimal(Value(affidavit, name), CultureInfo.InvariantCulture);

    private sealed class ReviewerConfirmationEvaluator : IApprovalPolicyEvaluator
    {
        public Task<ApprovalVerdict> EvaluateAsync(
            Affidavit affidavit, ConversationIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApprovalVerdict(ReviewRequirement.ReviewerConfirmation));
    }

    private sealed class AdmitEveryone : IDecisionAuthorizationPolicy
    {
        public Task<bool> MayDecideAsync(
            Principal principal, DocketEntry entry, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class SilentTransport : IStreamingTransport
    {
        public Task SendAsync(string connectionId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task BroadcastToGroupAsync(string groupId, TransportEvent eventType, object payload, CancellationToken ct)
            => Task.CompletedTask;

        public Task<EvidenceCardResponse> AwaitEvidenceCardResponseAsync(
            string sessionGroupId, Guid docketId, CancellationToken ct = default)
            => Task.FromException<EvidenceCardResponse>(new OperationCanceledException(ct));
    }
}
