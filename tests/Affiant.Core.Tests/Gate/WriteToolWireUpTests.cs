namespace Affiant.Core.Tests.Gate;

using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Core.Services;
using Affiant.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// A host that declares a write-capable tool and cannot route its review anywhere is refused at
/// startup, before any turn (CV-1) — two of the three ways the filter could previously pass a write
/// through unreviewed are visible from the composition root, and refusing them there is strictly
/// better than refusing the first write of the first conversation.
/// </summary>
public class WriteToolWireUpTests
{
    [Fact]
    public async Task AWriteToolWithNoReviewContextProvider_FailsAtStartup_NamingTheToolAndTheFix()
    {
        var validator = BuildValidator(
            services => services.AddAffiantTool<WidgetStrategy>("CreateWidget", Operation.WriteCreate, "Widget"),
            withReviewContextProvider: false);

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains(typeof(IReviewContextProvider).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("CreateWidget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteToolWithTheReviewLoopWired_StartsCleanly()
    {
        var validator = BuildValidator(
            services => services.AddAffiantTool<WidgetStrategy>("CreateWidget", Operation.WriteCreate, "Widget"));

        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task AHostThatDeclaresOnlyReadTools_IsUnaffected()
    {
        var validator = BuildValidator(
            services => services.AddAffiantReadTool("FindWidget", "Widget"),
            withReviewContextProvider: false);

        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    /// <summary>
    /// "No option turns the gate off for a tool it covers" (CV-1). The acknowledgment exists for a
    /// host that deliberately runs the read and inference half with no review loop — and a host that
    /// has declared a write-capable tool is, by its own declaration, not that host.
    /// </summary>
    [Fact]
    public async Task TheAcknowledgmentDoesNotTurnTheGateOff_ForADeclaredWriteTool()
    {
        var validator = BuildValidator(
            services => services.AddAffiantTool<WidgetStrategy>("CreateWidget", Operation.WriteCreate, "Widget"),
            withReviewContextProvider: false,
            configureCore: options => options.AcknowledgeMissingReviewWiring = true);

        var ex = await Assert.ThrowsAsync<AffiantStartupException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("AcknowledgeMissingReviewWiring does not apply", ex.Message, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static AffiantWireUpValidator BuildValidator(
        Action<IServiceCollection> wiring,
        bool withReviewContextProvider = true,
        Action<AffiantCoreOptions>? configureCore = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAffiantCore(configureCore);
        services.AddSingleton<IStreamingTransport>(new RecordingTransport());
        services.AddSingleton<IDocketStore>(new RecordingDocketStore());
        services.AddScoped<ReviewGate>();
        if (withReviewContextProvider)
            services.AddSingleton<IReviewContextProvider, UnusedReviewContextProvider>();
        wiring(services);

        return services.BuildServiceProvider()
            .GetServices<IHostedService>().OfType<AffiantWireUpValidator>().Single();
    }

    private sealed class UnusedReviewContextProvider : IReviewContextProvider
    {
        public ReviewContext? BuildReviewContext(WriteProposal proposal) => throw new NotSupportedException();
    }

    private sealed class WidgetStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } = [];
        public double? MinimumConfidenceThreshold => null;
    }
}
