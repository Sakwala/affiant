namespace Affiant.Testing.ComplianceHarness;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public static class ComplianceHarness
{
    public static ComplianceVerificationResult Verify(IServiceCollection services)
    {
        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAffiantToolRegistry>();

        var writeDescriptors = registry.All
            .Where(d => (d.Operation.Kind == "WriteCreate" || d.Operation.Kind == "WriteUpdate")
                     && d.InferenceStrategy is not null)
            .ToList();

        var fixtures = provider.GetServices<ITaskInferenceComplianceFixture>().ToList();

        // Phase 1: Discoverability — identify write strategies that have no paired fixture.
        var missingFixtures = writeDescriptors
            .Where(d => !fixtures.Any(f => f.Strategy == d.InferenceStrategy))
            .Select(d => new MissingFixture(d.InferenceStrategy!, d.FunctionName))
            .DistinctBy(mf => (mf.StrategyType, mf.FunctionName))
            .ToList();

        // Phase 2: Case execution + substance gating — run each InferenceFixtureCase for every
        // paired fixture, then assert substantive provenance over each produced Affidavit.
        var fixtureFailures = new List<FixtureFailure>();
        var substanceFailures = new List<SubstanceFailure>();

        var pairedStrategies = writeDescriptors
            .Select(d => d.InferenceStrategy!)
            .ToHashSet();

        foreach (var fixture in fixtures.Where(f => pairedStrategies.Contains(f.Strategy)))
        {
            // First matching descriptor supplies FunctionName and Operation.Kind for the projection call.
            var descriptor = writeDescriptors.First(d => d.InferenceStrategy == fixture.Strategy);

            var producedAny = false;
            var producedSubstantive = false;

            foreach (var fixtureCase in fixture.Cases)
            {
                var outcome = ExecuteFixtureCase(
                    provider, fixture, fixtureCase, descriptor, fixtureFailures, substanceFailures);

                if (outcome != CaseOutcome.NoAffidavit)
                    producedAny = true;
                if (outcome == CaseOutcome.Substantive)
                    producedSubstantive = true;
            }

            // Per-fixture substance gate (generalizes ExtractionAudit_f7d3ebe_ProvenancePreservationTests):
            // a strategy that only ever yields hollow Affidavits is the b72c1fa regression. At least one
            // case must demonstrate the strategy CAN produce substantive provenance. Only raised when the
            // fixture actually produced Affidavits (all-erroring cases are already in fixtureFailures).
            if (producedAny && !producedSubstantive)
            {
                substanceFailures.Add(new SubstanceFailure(
                    fixture.Strategy,
                    "(all cases)",
                    "(affidavit)",
                    "no fixture case produced a substantive Affidavit — every case is hollow " +
                    "(all fields carry ProvenanceSource.Empty). At least one case must demonstrate that " +
                    "the strategy can produce substantive provenance (b72c1fa regression guard; generalizes " +
                    "ExtractionAudit_f7d3ebe_ProvenancePreservationTests)."));
            }
        }

        return new ComplianceVerificationResult(
            Passed: missingFixtures.Count == 0 && fixtureFailures.Count == 0 && substanceFailures.Count == 0,
            MissingFixtures: missingFixtures,
            FixtureFailures: fixtureFailures,
            SubstanceFailures: substanceFailures);
    }

    /// <summary>
    /// The executable guard against the b72c1fa regression class — an Affidavit that is
    /// structurally valid but substantively hollow while every hand-written fixture assertion
    /// stays green. Asserts, for a single produced <paramref name="affidavit"/>:
    /// <list type="number">
    /// <item><c>Fields</c> is non-empty (guards the empty-Affidavit form of b72c1fa).</item>
    /// <item>Every field carries a provenance chain (Rule 7 — never omit provenance).</item>
    /// <item>Any field that carries a value is sworn — a populated value with
    /// <see cref="ProvenanceSource.Empty"/> is the hollow signature and fails.</item>
    /// <item>Every strategy field declared <c>Required=true</c> projects to
    /// <c>AffidavitField.IsMandatory == true</c>.</item>
    /// </list>
    ///
    /// This method deliberately does NOT require <c>Required</c> fields to be populated — an
    /// empty mandatory field is a reviewer-UI concern, not a projection-truthfulness concern.
    /// It also does not forbid <see cref="ProvenanceSource.Empty"/> outright: a below-threshold
    /// case that legitimately infers nothing yields an all-Empty Affidavit and must pass here
    /// (the "must produce substance somewhere" invariant is enforced per-fixture in <see cref="Verify"/>).
    ///
    /// Runs by default as part of <see cref="Verify"/>. Also public so the same predicate can be
    /// reused by other callers (e.g. the auto-issue-detection explorer) without duplicating it.
    /// </summary>
    public static IReadOnlyList<SubstanceFailure> AssertProvenanceIsSubstantive(
        ITaskInferenceStrategy strategy,
        string fixtureCaseName,
        Affidavit affidavit)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(affidavit);

        var strategyType = strategy.GetType();
        var failures = new List<SubstanceFailure>();

        // Check 1: Fields non-empty — the empty-Affidavit form of b72c1fa.
        if (affidavit.Fields.Length == 0)
        {
            failures.Add(new SubstanceFailure(
                strategyType, fixtureCaseName, "(affidavit)",
                "Affidavit.Fields is empty — no sworn fields were produced (b72c1fa hollow-Affidavit regression)."));
            return failures;
        }

        var declaredFields = strategy.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        foreach (var field in affidavit.Fields)
        {
            // Check 2: provenance chain present (Rule 7). A null chain means the field was emitted
            // without provenance — indistinguishable from "the framework forgot to track it".
            if (field.Provenance is null || field.Provenance.Current is null)
            {
                failures.Add(new SubstanceFailure(
                    strategyType, fixtureCaseName, field.Name,
                    "field carries no provenance chain — Rule 7 requires every field carry provenance " +
                    "(tag ProvenanceSource.Empty rather than omitting)."));
                continue;
            }

            var source = field.Provenance.Current.Source;

            // Check 3: a populated value must be sworn. A value present with Empty provenance is the
            // exact hollow signature — the field asserts a value but swears nothing about its origin.
            if (HasValue(field.Value) && source == ProvenanceSource.Empty)
            {
                failures.Add(new SubstanceFailure(
                    strategyType, fixtureCaseName, field.Name,
                    $"field carries a value ('{DescribeValue(field.Value)}') but ProvenanceSource.Empty — " +
                    "a value without provenance is the b72c1fa hollow signature."));
            }

            // Check 4: Required strategy field must project to a mandatory Affidavit field.
            if (declaredFields.TryGetValue(field.Name, out var declared)
                && declared.Required
                && !field.IsMandatory)
            {
                failures.Add(new SubstanceFailure(
                    strategyType, fixtureCaseName, field.Name,
                    "strategy declares this field Required=true but the projected " +
                    "AffidavitField.IsMandatory is false."));
            }
        }

        return failures;
    }

    private enum CaseOutcome
    {
        NoAffidavit,
        Hollow,
        Substantive,
    }

    private static CaseOutcome ExecuteFixtureCase(
        IServiceProvider provider,
        ITaskInferenceComplianceFixture fixture,
        InferenceFixtureCase fixtureCase,
        AffiantToolDescriptor descriptor,
        List<FixtureFailure> fixtureFailures,
        List<SubstanceFailure> substanceFailures)
    {
        // Per design note 4: absent port → report as failure, do not throw.
        var completionPort = provider.GetService<IInferenceCompletionPort>();
        if (completionPort is null)
        {
            fixtureFailures.Add(new FixtureFailure(
                fixture.Strategy,
                fixtureCase.Name,
                "no IInferenceCompletionPort registered — cannot execute fixture case"));
            return CaseOutcome.NoAffidavit;
        }

        var strategyObj = provider.GetService(fixture.Strategy);
        if (strategyObj is not ITaskInferenceStrategy strategy)
        {
            fixtureFailures.Add(new FixtureFailure(
                fixture.Strategy,
                fixtureCase.Name,
                $"Strategy type {fixture.Strategy.Name} not registered in DI or does not implement ITaskInferenceStrategy"));
            return CaseOutcome.NoAffidavit;
        }

        try
        {
            var loggerFactory = provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;

            // Create isolated fabric for this case — each case must be independent.
            var fabric = new ContextFabric();
            var step = new TaskInferenceStep(
                fabric,
                loggerFactory.CreateLogger<TaskInferenceStep>());
            var runner = new TaskInferenceRunner(
                completionPort,
                fabric,
                step,
                loggerFactory.CreateLogger<TaskInferenceRunner>());

            try
            {
                runner.RunAsync(
                    strategy,
                    fixtureCase.History,
                    descriptor.FunctionName,
                    fixtureCase.Arguments)
                    .GetAwaiter().GetResult();
            }
            catch (AggregateException aex)
            {
                var inner = aex.InnerException ?? aex;
                fixtureFailures.Add(new FixtureFailure(
                    fixture.Strategy,
                    fixtureCase.Name,
                    $"Exception during case execution: {inner.GetType().Name}: {inner.Message}"));
                return CaseOutcome.NoAffidavit;
            }

            var eventStream = provider.GetRequiredService<IObservabilityEventStream<AffidavitEmittedEvent>>();
            var deterministicSources = provider.GetServices<IDeterministicFieldSource>();
            var projection = new SchemaDrivenAffidavitProjection(
                strategy,
                deterministicSources,
                loggerFactory.CreateLogger<SchemaDrivenAffidavitProjection>(),
                eventStream);

            var affidavit = projection.Project(fabric, descriptor.Operation.Kind, Array.Empty<string>());

            // Substance gate runs on the produced Affidavit regardless of what the fixture asserts —
            // this is the assertion-independent guard against b72c1fa.
            substanceFailures.AddRange(
                AssertProvenanceIsSubstantive(strategy, fixtureCase.Name, affidavit));

            var outcome = IsSubstantive(affidavit) ? CaseOutcome.Substantive : CaseOutcome.Hollow;

            bool assertionPassed;
            try
            {
                assertionPassed = fixtureCase.Assertion(affidavit);
            }
            catch (Exception ex)
            {
                fixtureFailures.Add(new FixtureFailure(
                    fixture.Strategy,
                    fixtureCase.Name,
                    $"Assertion threw: {ex.GetType().Name}: {ex.Message}"));
                return outcome;
            }

            if (!assertionPassed)
            {
                fixtureFailures.Add(new FixtureFailure(
                    fixture.Strategy,
                    fixtureCase.Name,
                    "Assertion returned false"));
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            fixtureFailures.Add(new FixtureFailure(
                fixture.Strategy,
                fixtureCase.Name,
                $"Exception during case execution: {ex.GetType().Name}: {ex.Message}"));
            return CaseOutcome.NoAffidavit;
        }
    }

    private static bool IsSubstantive(Affidavit affidavit) =>
        affidavit.Fields.Any(f => f.Provenance?.Current is { Source: not ProvenanceSource.Empty });

    private static bool HasValue(object? value) =>
        value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s));

    private static string DescribeValue(object? value)
    {
        var text = value?.ToString() ?? "null";
        return text.Length > 60 ? text[..57] + "..." : text;
    }
}
