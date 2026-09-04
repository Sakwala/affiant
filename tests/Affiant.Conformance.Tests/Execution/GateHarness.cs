using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Conformance.Tests.Model;
using Affiant.Conformance.Tests.Ports;
using Affiant.Core.Extensions;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Affiant.Docket.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Affiant.Conformance.Tests.Execution;

/// <summary>
/// The framework, wired up the way a host wires it, from one fixture's <c>given.gate</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Docket store and the transport are singletons — one docket, one push channel, shared by
/// every conversation, as they must be. Everything the framework registers per conversation turn
/// (the context fabric, the policy evaluator, the gate) comes from a DI scope <b>per conversation
/// id</b>, which is the scoping a host performs and the only place conversation isolation (GT-2)
/// can come from in this release.
/// </para>
/// <para>
/// <b>The clock is a seam.</b> A fixture's <c>clock</c> drives a <see cref="TimeProvider"/> the
/// harness registers, and every step's <c>at</c> moves it, so every instant the framework stamps is
/// the fixture's own and nothing reads the wall clock.
/// </para>
/// </remarks>
internal sealed class GateHarness : IDisposable
{
    private readonly Dictionary<string, IServiceScope> _conversations = new(StringComparer.Ordinal);
    private readonly ServiceProvider _root;
    private readonly FixtureClock _clock;

    private GateHarness(ServiceProvider root, GateSpec gate, DateTimeOffset clock)
    {
        _root = root;
        Spec = gate;
        _clock = root.GetRequiredService<FixtureClock>();
        _clock.Now = clock;
        Store = root.GetRequiredService<InMemoryDocketStore>();
        Transport = root.GetRequiredService<RecordingTransport>();
        Executor = root.GetRequiredService<TripwireWriteExecutor>();
        Authorization = root.GetRequiredService<FixtureAuthorization>();
        Telemetry = root.GetRequiredService<TelemetrySink>();
        Options = root.GetRequiredService<AffiantCoreOptions>();
        Authorization.Now = () => Clock;
        Policies = root.GetServices<IApprovalPolicy>().OfType<FixturePolicy>().ToArray();
    }

    /// <summary>The wiring this harness was built from.</summary>
    public GateSpec Spec { get; }

    /// <summary>The fixture's clock. It moves only when a step moves it.</summary>
    public DateTimeOffset Clock
    {
        get => _clock.Now;
        set => _clock.Now = value;
    }

    public InMemoryDocketStore Store { get; }

    public RecordingTransport Transport { get; }

    public TripwireWriteExecutor Executor { get; }

    public FixtureAuthorization Authorization { get; }

    public TelemetrySink Telemetry { get; }

    public AffiantCoreOptions Options { get; }

    public IReadOnlyList<FixturePolicy> Policies { get; }

    /// <summary>Build the gate a fixture describes, or throw the refusal it produces at wire-up (CV-1).</summary>
    public static GateHarness Build(GateSpec gate, DateTimeOffset clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var options = new AffiantCoreOptions
        {
            DefaultDocketTtl = TimeSpan.FromMilliseconds(gate.DefaultTtlMs),

            // The sweep's warning phase is a different rule from the ones the suite is about, and a
            // warning broadcast in the middle of an expiry fixture would show up as an unexplained
            // push. Off unless a fixture asks for it.
            DocketExpiryWarningWindow = TimeSpan.Zero,
            AcknowledgeMissingReviewWiring = true,
        };

        services.AddSingleton(options);
        services.AddSingleton<FixtureClock>();
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FixtureClock>());
        services.AddSingleton<InMemoryDocketStore>();
        services.AddSingleton<IDocketStore>(sp => sp.GetRequiredService<InMemoryDocketStore>());
        services.AddSingleton<RecordingTransport>();
        services.AddSingleton<IStreamingTransport>(sp => sp.GetRequiredService<RecordingTransport>());
        services.AddSingleton<TripwireWriteExecutor>();
        services.AddSingleton<IWriteExecutor>(sp => sp.GetRequiredService<TripwireWriteExecutor>());
        services.AddSingleton(new FixtureAuthorization(gate.Authorization));
        services.AddSingleton<IToolAuthorizationPolicy>(sp => sp.GetRequiredService<FixtureAuthorization>());

        // AZ-2: `given.gate.authorization` is who may DECIDE, so it binds the decision port as well
        // as the tool port. A driver that offered only the tool port would report "the gate never
        // asked" where the truth was "the driver never offered".
        services.AddSingleton<IDecisionAuthorizationPolicy>(sp => sp.GetRequiredService<FixtureAuthorization>());
        services.AddSingleton<TelemetrySink>();
        services.AddSingleton<IObservabilityEventStream<AffidavitEmittedEvent>, InMemoryObservabilityEventStream<AffidavitEmittedEvent>>();
        services.AddSingleton<IInferenceCompletionPort>(new ScriptedInference(gate.Inference));

        // AF-3: what the host's entities hold now. The projection reads this table, so a fixture
        // states the world and the previous values are whatever the table says.
        if (gate.EntitiesStated)
        {
            services.AddSingleton<IPreviousValueSource>(new FixtureEntities(gate.Entities));
        }

        // The approval chain, in the fixture's order (AZ-4). The evaluator walks IEnumerable in
        // registration order, so the order the fixture states is the order the chain runs in.
        foreach (var policy in gate.Policies)
        {
            services.AddSingleton<IApprovalPolicy>(new FixturePolicy(policy, gate.RiskScorer));
        }

        // The deterministic resolvers of PV-2, one per field an interceptor names.
        foreach (var interceptor in gate.Interceptors)
        {
            foreach (var (field, resolved) in interceptor.Fields)
            {
                services.AddSingleton<IFieldResolver>(new FixtureFieldResolver(field, resolved));
            }
        }

        services.AddScoped<ContextFabric>();
        services.AddScoped<IContextFabric>(sp => sp.GetRequiredService<ContextFabric>());
        services.AddScoped<Affiant.Core.Filters.TaskInferenceStep>();
        services.AddScoped<TaskInferenceRunner>();
        services.AddScoped<ApprovalPolicyEvaluator>();
        services.AddScoped<IApprovalPolicyEvaluator>(sp => sp.GetRequiredService<ApprovalPolicyEvaluator>());
        services.AddScoped<ReviewGate>();
        services.AddSingleton<Affiant.Docket.Services.DocketExpiryService>();

        var root = services.BuildServiceProvider();

        // Subscribing here rather than inside the sink keeps the sink free of a framework
        // dependency, and makes the one genuinely injectable telemetry signal this release has
        // (the projection's typed event) visible beside the activity names.
        var sink = root.GetRequiredService<TelemetrySink>();
        root.GetRequiredService<IObservabilityEventStream<AffidavitEmittedEvent>>()
            .Subscribe(_ => sink.Record("affidavit.emitted"));

        return new GateHarness(root, gate, clock);
    }

    /// <summary>The services for one conversation — a scope per conversation id, created on first use.</summary>
    public IServiceProvider Conversation(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var scope))
        {
            scope = _root.CreateScope();
            _conversations[conversationId] = scope;
        }

        return scope.ServiceProvider;
    }

    /// <summary>The gate for one conversation.</summary>
    public ReviewGate GateFor(string conversationId) => Conversation(conversationId).GetRequiredService<ReviewGate>();

    /// <summary>The framework's expiry sweep. Unpaged and wall-clock driven — see the type remarks.</summary>
    public Affiant.Docket.Services.DocketExpiryService Sweep => _root.GetRequiredService<Affiant.Docket.Services.DocketExpiryService>();

    public void Dispose()
    {
        foreach (var scope in _conversations.Values)
        {
            scope.Dispose();
        }

        _root.Dispose();
    }
}
