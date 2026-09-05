namespace Affiant.Core.Tests.Gate;

using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// The filter fails closed (CV-1, CV-2 — closes Sakwala/affiant#75). Every branch that used to
/// return quietly at debug-log level, leaving the raw proposal on the result for the model to report
/// as a completed write, is now a refusal carrying <c>wireup-invalid</c>.
///
/// <para>
/// The three branches are: no review-context provider registered, a provider that cannot build a
/// context for this call, and no <c>ReviewGate</c> registered. The first and third are also refused
/// at startup — see <c>WriteToolWireUpTests</c> — and this is the backstop for the second, which
/// only a live request can know, and for a container that cannot be enumerated at startup.
/// </para>
/// </summary>
public class ReviewGateFilterFailClosedTests
{
    private static readonly ReviewGateFilter Filter = new(NullLogger<ReviewGateFilter>.Instance);

    // ── The three fail-open branches are refusals ────────────────────────────────────────────

    [Fact]
    public async Task NoReviewContextProviderRegistered_RefusesRatherThanPassingTheProposalThrough()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        var result = await RunWrite(services);

        var error = AssertError(result);
        Assert.Equal(ToolErrorCodes.WireUpInvalid, error.Code);
        Assert.Contains("NOT filed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderThatCannotBuildAContextForThisCall_Refuses()
    {
        var services = Stack(new RecordingDocketStore(), providerReturnsNull: true).BuildServiceProvider();
        using var scope = services.CreateScope();

        var result = await RunWrite(scope.ServiceProvider);

        Assert.Equal(ToolErrorCodes.WireUpInvalid, AssertError(result).Code);
    }

    [Fact]
    public async Task NoReviewGateRegistered_Refuses()
    {
        // A provider but no gate: nothing to file the proposal with.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IReviewContextProvider>(new FixedProvider(ReviewContext()));
        var provider = services.BuildServiceProvider();

        var result = await RunWrite(provider);

        Assert.Equal(ToolErrorCodes.WireUpInvalid, AssertError(result).Code);
    }

    [Fact]
    public async Task ARefusedWireUp_DoesNotEndTheTurn()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = Context(services);

        await Filter.OnToolInvocationAsync(ctx, c => { c.Result = WriteProposalJson(); return Task.CompletedTask; });

        // Nothing was filed, so there is no card for anyone to look at: the model should see this
        // like any other typed tool failure rather than be told a review is pending.
        Assert.False(ctx.Terminate);
    }

    // ── A declared write tool that returns something other than a proposal ───────────────────

    [Fact]
    public async Task ADeclaredWriteToolReturningANonProposal_IsRefused_NotSkipped()
    {
        var services = Stack(new RecordingDocketStore(), declareWriteTool: true).BuildServiceProvider();
        using var scope = services.CreateScope();

        var result = await Run(scope.ServiceProvider, "wrote the row, all done");

        var error = AssertError(result);
        Assert.Equal(ToolErrorCodes.WireUpInvalid, error.Code);
        Assert.Contains("declared write-capable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadToolsResult_StillPassesThroughUntouched()
    {
        var services = Stack(new RecordingDocketStore(), declareReadTool: true).BuildServiceProvider();
        using var scope = services.CreateScope();

        var result = await Run(scope.ServiceProvider, "widget:blue");

        Assert.Equal("widget:blue", result);
    }

    [Fact]
    public async Task AToolTheRegistryDoesNotKnow_StillPassesThroughUntouched()
    {
        // An absent declaration is the startup validators' business, not a refusal invented here
        // from a registry that was never populated.
        var services = Stack(new RecordingDocketStore()).BuildServiceProvider();
        using var scope = services.CreateScope();

        Assert.Equal("plain text", await Run(scope.ServiceProvider, "plain text"));
    }

    // ── A gate refusal reaches the model as the error arm ────────────────────────────────────

    [Fact]
    public async Task ASubstanceRefusal_BecomesTheToolsErrorResult_CarryingItsOwnCode()
    {
        var hollow = TestAffidavits.Of(TestAffidavits.Field("amount", 4200, ProvenanceTag.Empty));
        var store = new RecordingDocketStore();
        var services = Stack(store, context: ReviewContext(hollow)).BuildServiceProvider();
        using var scope = services.CreateScope();

        var result = await RunWrite(scope.ServiceProvider);

        var error = AssertError(result);
        Assert.Equal(ToolErrorCodes.SubstanceRefused, error.Code);
        Assert.False(error.Retryable);
        Assert.Empty(store.Filed);
    }

    [Fact]
    public async Task APolicyRefusal_BecomesTheToolsErrorResult_CarryingWireUpInvalid()
    {
        var store = new RecordingDocketStore();
        var services = Stack(store, policy: new ThrowingPolicy()).BuildServiceProvider();
        using var scope = services.CreateScope();

        var result = await RunWrite(scope.ServiceProvider);

        Assert.Equal(ToolErrorCodes.WireUpInvalid, AssertError(result).Code);
        Assert.Empty(store.Filed);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private const string ToolName = "CreateOrder";

    private static string WriteProposalJson() =>
        new WriteProposal(ToolName, DateTimeOffset.UtcNow, TestAffidavits.Substantive()).ToJsonString();

    private static Task<object?> RunWrite(IServiceProvider services) => Run(services, WriteProposalJson());

    private static async Task<object?> Run(IServiceProvider services, object? toolResult)
    {
        var ctx = Context(services);
        await Filter.OnToolInvocationAsync(ctx, c => { c.Result = toolResult; return Task.CompletedTask; });
        return ctx.Result;
    }

    private static ToolInvocationContext Context(IServiceProvider services) => new()
    {
        FunctionName = ToolName,
        PluginName = "Orders",
        Arguments = new Dictionary<string, object?>(),
        Services = services,
    };

    private static ToolError AssertError(object? result)
    {
        var json = Assert.IsType<string>(result);
        var envelope = JsonSerializer.Deserialize<ToolEnvelope>(
            json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return Assert.IsType<ToolError>(envelope);
    }

    private static ReviewContext ReviewContext(Affidavit? affidavit = null) => new(
        SessionId: "session-1",
        TenantId: "tenant-1",
        UserId: "user-1",
        ReviewerUserId: "reviewer-1",
        Affidavit: affidavit ?? TestAffidavits.Substantive());

    private static ServiceCollection Stack(
        RecordingDocketStore store,
        ReviewContext? context = null,
        IApprovalPolicy? policy = null,
        bool declareWriteTool = false,
        bool declareReadTool = false,
        bool providerReturnsNull = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(o => o.EnableObservability = false);
        services.AddScoped<ReviewGate>();
        services.AddSingleton<IStreamingTransport>(new RecordingTransport());
        services.AddSingleton<IDocketStore>(store);
        services.AddSingleton<IApprovalPolicyEvaluator, ApprovalPolicyEvaluator>();
        if (policy is not null) services.AddSingleton(policy);
        services.AddSingleton<IReviewContextProvider>(
            new FixedProvider(providerReturnsNull ? null : context ?? ReviewContext()));

        if (declareWriteTool)
            services.AddAffiantTool<OrderStrategy>(ToolName, Operation.WriteCreate, "Order", "Orders");
        if (declareReadTool)
            services.AddAffiantReadTool(ToolName, "Order", "Orders");

        return services;
    }

    private sealed class FixedProvider(ReviewContext? context) : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => context;
    }

    private sealed class OrderStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Order";
        public IReadOnlyList<TaskInferenceField> Fields { get; } = [];
        public double? MinimumConfidenceThreshold => null;
    }
}
