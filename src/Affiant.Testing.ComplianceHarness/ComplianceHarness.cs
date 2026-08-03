namespace Affiant.Testing.ComplianceHarness;

using System.Reflection;
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

    /// <summary>
    /// Opt-in parity check between a strategy's card fields and what the write path actually
    /// consumes. Unlike <see cref="Verify"/>, this does <b>not</b> run automatically — a host
    /// calls it directly (e.g. one line inside its own test), typically passing the parameter
    /// names of the domain method the write tool ultimately calls.
    ///
    /// Reports two orthogonal problems:
    /// <list type="bullet">
    /// <item><b>Errors</b> — a <c>Projected == true</c> strategy field the write path never reads.
    /// Such a field is sworn on the Evidence Card but has no effect on the write, which is either
    /// dead weight on the card or a sign the field should have been declared an extraction field
    /// (<c>Projected: false</c>) instead. <c>Projected == false</c> fields are exempt — they exist
    /// to feed resolvers/business logic, not to be consumed verbatim by the write path.</item>
    /// <item><b>Warnings</b> — a name in <paramref name="writeConsumedFieldNames"/> that the
    /// strategy never declares at all. Not necessarily wrong (the write path may read fields from
    /// elsewhere), so this is informational only and never fails <see cref="FieldSetParityResult.Passed"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="strategy">The strategy whose <see cref="ITaskInferenceStrategy.Fields"/> declare the card's shape.</param>
    /// <param name="writeConsumedFieldNames">The field names the write path actually reads (e.g. the domain write method's parameter names).</param>
    public static FieldSetParityResult AssertFieldSetParity(
        ITaskInferenceStrategy strategy,
        IReadOnlyCollection<string> writeConsumedFieldNames)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(writeConsumedFieldNames);

        var consumed = new HashSet<string>(writeConsumedFieldNames, StringComparer.Ordinal);
        var declared = new HashSet<string>(strategy.Fields.Select(f => f.Name), StringComparer.Ordinal);

        var errors = strategy.Fields
            .Where(f => f.Projected && !consumed.Contains(f.Name))
            .Select(f => new FieldParityViolation(
                f.Name,
                $"card field '{f.Name}' is not part of the write contract — make it an extraction " +
                "field (Projected=false) or remove it."))
            .ToList();

        var warnings = consumed
            .Where(name => !declared.Contains(name))
            .Select(name => new FieldParityViolation(
                name,
                $"write path consumes field '{name}', which strategy '{strategy.GetType().Name}' does not declare."))
            .ToList();

        return new FieldSetParityResult(errors.Count == 0, errors, warnings);
    }

    /// <summary>
    /// Generalizes <see cref="AssertFieldSetParity"/> to the LLM tool-name boundary (Area 2 gate
    /// ruling 2, "C-prime": every LLM-exposed tool name is a deliberate <c>ToolNames</c>-style
    /// constant). Asserts a bijection between <paramref name="toolNamesType"/>'s declared
    /// constants and the tool names a host actually exposes to the LLM.
    ///
    /// <para><b>Why this takes an already-resolved name list instead of reflecting for you:</b>
    /// this package depends only on <c>Affiant.Abstractions</c>/<c>Affiant.Core</c> (see the
    /// framework spec's rationale — it is what lets one compliance suite run against both
    /// interception backends), so it cannot itself walk <c>[KernelFunction]</c>-attributed methods
    /// (that requires referencing <c>Microsoft.SemanticKernel</c>) or an
    /// <c>AffiantToolCatalog</c> (that requires referencing the sibling adapter package
    /// <c>Affiant.AgentFramework</c> — adapters may not reference each other either). The caller
    /// performs the one adapter-specific reflection step and passes the resulting names here:</para>
    /// <list type="bullet">
    /// <item><b>SK:</b> <c>type.GetMethods(...).Select(m =&gt; m.GetCustomAttribute&lt;KernelFunctionAttribute&gt;()?.Name)</c>
    /// — the attribute's explicit <c>Name</c>. SK's own default-naming fallback (bare method name,
    /// optionally minus a trailing "Async") is deliberately NOT replicated here: a tool exposed
    /// under that fallback is exactly the drift this check exists to catch, so pass the raw
    /// effective name through rather than pre-normalizing it away.</item>
    /// <item><b>MAF:</b> <c>AffiantToolCatalog.FromType&lt;T&gt;().Descriptors.Select(d =&gt; d.FunctionName)</c>
    /// — already the post-<c>[AffiantToolName]</c>-override effective name.</item>
    /// </list>
    ///
    /// This is capable of replacing a host's bespoke reflection exhaustiveness test (e.g. the
    /// Area 2 P1 <c>ToolNamesExhaustivenessTests</c> pattern) without losing any assertion: that
    /// pattern's "every exposed tool's effective name is a ToolNames member" and "every ToolNames
    /// member maps to exactly one exposed tool" are exactly <see cref="ToolNameParityResult.UndeclaredTools"/>
    /// and <see cref="ToolNameParityResult.OrphanConstants"/>/<see cref="ToolNameParityResult.AmbiguousConstants"/>
    /// below. A host-specific concern that pattern also checks — e.g. "every exposed name is
    /// snake_case" — is a content assertion orthogonal to parity and stays a host-side test.
    /// </summary>
    /// <param name="toolNamesType">
    /// A type whose <c>public const string</c> fields are the declared tool-name registry (a
    /// <c>ToolNames</c>-style class).
    /// </param>
    /// <param name="exposedToolNames">
    /// The LLM-visible name of every tool the host currently registers, resolved via the
    /// adapter-specific reflection described in the remarks above. Duplicate entries are
    /// meaningful (they can produce an <see cref="ToolNameParityResult.AmbiguousConstants"/>
    /// finding) — do not de-duplicate before calling.
    /// </param>
    /// <param name="exemptConstants">
    /// <paramref name="toolNamesType"/> member names deliberately excluded from the
    /// "must map to exactly one exposed tool" direction only (e.g. a name reserved for a tool
    /// behind an unshipped feature flag). An exempted constant is still required to NOT collide
    /// with another tool's name if it happens to be exposed — exemption only silences the
    /// zero-matches (orphan) case. Defaults to empty.
    /// </param>
    public static ToolNameParityResult AssertToolNameRegistryParity(
        Type toolNamesType,
        IReadOnlyCollection<string> exposedToolNames,
        IReadOnlyCollection<string>? exemptConstants = null)
    {
        ArgumentNullException.ThrowIfNull(toolNamesType);
        ArgumentNullException.ThrowIfNull(exposedToolNames);

        var exempt = new HashSet<string>(exemptConstants ?? [], StringComparer.Ordinal);
        var declared = GetConstStringMembers(toolNamesType);
        var declaredValues = new HashSet<string>(declared.Values, StringComparer.Ordinal);

        var undeclaredTools = exposedToolNames
            .Distinct(StringComparer.Ordinal)
            .Where(name => !declaredValues.Contains(name))
            .Select(name => new ParityViolation(
                name,
                $"tool \"{name}\" is exposed to the LLM but is not the value of any " +
                $"{toolNamesType.Name} constant — add a constant and feed it into the tool's " +
                "declaration site (or fix a raw-literal/default-named tool)."))
            .ToList();

        var orphanConstants = new List<ParityViolation>();
        var ambiguousConstants = new List<ParityViolation>();

        foreach (var (memberName, value) in declared)
        {
            var matches = exposedToolNames.Count(name => name == value);

            if (matches == 0)
            {
                if (exempt.Contains(memberName)) continue;

                orphanConstants.Add(new ParityViolation(
                    memberName,
                    $"{toolNamesType.Name}.{memberName} (\"{value}\") does not match any exposed " +
                    "tool — orphaned constant left behind after a rename or removal."));
            }
            else if (matches > 1)
            {
                ambiguousConstants.Add(new ParityViolation(
                    memberName,
                    $"{toolNamesType.Name}.{memberName} (\"{value}\") matches {matches} exposed " +
                    "tools — two tools cannot share one LLM-visible name."));
            }
        }

        return new ToolNameParityResult(
            undeclaredTools.Count == 0 && orphanConstants.Count == 0 && ambiguousConstants.Count == 0,
            undeclaredTools, orphanConstants, ambiguousConstants);
    }

    /// <summary>
    /// Generalizes <see cref="AssertFieldSetParity"/> to context-fabric keys (Area 2 paper P2).
    /// Asserts a host's <paramref name="fabricKeysType"/> constants agree with the keys its
    /// extractors/resolvers/plugins actually read from or write to <c>IContextFabric</c>: no
    /// orphan constants, no undeclared (bare-literal) keys.
    ///
    /// <para><b>Why "live set" acquisition is an explicit parameter, not introspection:</b> unlike
    /// tool names, which funnel through one adapter-specific enumeration point (a plugin type's
    /// methods, or a tool catalog), fabric keys are read and written at arbitrary call sites
    /// across a host's <c>IContextExtractor</c>/<c>IFieldResolver</c>/<c>IDeterministicFieldSource</c>
    /// implementations and plugin bodies, into an untyped <c>IContextFabric</c> dictionary with no
    /// central registry to reflect over. There is no honest way for this method to discover that
    /// set at runtime. <paramref name="liveKeys"/> is therefore a caller-supplied enumeration —
    /// the same tradeoff <see cref="AssertFieldSetParity"/> already makes for
    /// <paramref name="liveKeys"/>'s sibling parameter there,
    /// <c>writeConsumedFieldNames</c> — typically produced by grepping or by hand-walking the
    /// host's extractor/resolver source for every fabric-key literal actually used. Consequence:
    /// this check is only as good as that enumeration; a call site added later without updating
    /// the list it feeds here is undetected.</para>
    /// </summary>
    /// <param name="fabricKeysType">
    /// A type whose <c>public const string</c> fields are the declared fabric-key registry (a
    /// <c>FabricKeys</c>-style class).
    /// </param>
    /// <param name="liveKeys">
    /// Every fabric key the host's extractors/resolvers/plugins actually read or write —
    /// caller-supplied (see remarks); duplicates are harmless (deduplicated internally).
    /// </param>
    /// <param name="exemptConstants">
    /// <paramref name="fabricKeysType"/> member names deliberately excluded from the orphan check
    /// (e.g. a key reserved for an extractor not yet wired up). Defaults to empty.
    /// </param>
    public static FabricKeyParityResult AssertFabricKeyParity(
        Type fabricKeysType,
        IReadOnlyCollection<string> liveKeys,
        IReadOnlyCollection<string>? exemptConstants = null)
    {
        ArgumentNullException.ThrowIfNull(fabricKeysType);
        ArgumentNullException.ThrowIfNull(liveKeys);

        var exempt = new HashSet<string>(exemptConstants ?? [], StringComparer.Ordinal);
        var declared = GetConstStringMembers(fabricKeysType);
        var declaredValues = new HashSet<string>(declared.Values, StringComparer.Ordinal);
        var live = new HashSet<string>(liveKeys, StringComparer.Ordinal);

        var orphanConstants = declared
            .Where(kv => !exempt.Contains(kv.Key) && !live.Contains(kv.Value))
            .Select(kv => new ParityViolation(
                kv.Key,
                $"{fabricKeysType.Name}.{kv.Key} (\"{kv.Value}\") is declared but does not match " +
                "any supplied live key — orphaned constant, or the live-key enumeration is stale."))
            .ToList();

        var undeclaredKeys = live
            .Where(key => !declaredValues.Contains(key))
            .Select(key => new ParityViolation(
                key,
                $"fabric key \"{key}\" is read/written by the host but is not the value of any " +
                $"{fabricKeysType.Name} constant — a bare literal escaped the registry."))
            .ToList();

        return new FabricKeyParityResult(
            orphanConstants.Count == 0 && undeclaredKeys.Count == 0, orphanConstants, undeclaredKeys);
    }

    /// <summary>
    /// Generalizes <see cref="AssertFabricKeyParity"/> to <c>ToolError.Code</c> values (area-3 P2
    /// ruling 4 — the Area-2 harness treatment applied to the framework's <c>ToolError</c> codes,
    /// which had no shared constants class, no registry, and no contract test:
    /// <c>docs/architecture-review/area-3-tool-calling-reliability.md</c> V6). Asserts a host's
    /// <paramref name="toolErrorCodesType"/> constants agree with the codes actually emitted
    /// somewhere in the process (framework emission sites, plus any host emission sites the caller
    /// chooses to enumerate): no orphan constants, no undeclared (bare-literal) codes.
    ///
    /// <para><b>Additive, host-facing API:</b> the framework declares its own registry
    /// (<c>Affiant.Abstractions.Models.ToolErrorCodes</c>) and can self-check it (see that type's
    /// remarks); a host declares its own <c>ToolErrorCodes</c>-style class covering its domain codes
    /// and calls this method the same way it would call <see cref="AssertToolNameRegistryParity"/>
    /// or <see cref="AssertFabricKeyParity"/> — nothing here requires a host to migrate to the
    /// framework's registry, and nothing about declaring this method breaks a host that has not
    /// adopted it yet (it is opt-in, exactly like its two siblings).</para>
    ///
    /// <para><b>Why "live set" acquisition is an explicit parameter, not introspection:</b> same
    /// tradeoff as <see cref="AssertFabricKeyParity"/>'s <c>liveKeys</c> — <c>ToolError.Code</c>
    /// values are produced at arbitrary call sites (exception-mapping switches, filters that
    /// directly construct a <see cref="ToolError"/>, hand-written JSON string
    /// literals bypassing the type entirely) with no central registry to reflect over. There is no
    /// honest way for this method to discover that set at runtime; <paramref name="emittedCodes"/>
    /// is a caller-supplied enumeration, typically produced by grepping the codebase for every
    /// distinct <c>Code:</c>/<c>"code":</c> emission site.</para>
    ///
    /// <para><b>Division of labor (area-3 P2 fix round, finding 2).</b> Because
    /// <paramref name="emittedCodes"/> is caller-supplied, this method can only ever detect
    /// ORPHANED constants (declared but not present in the supplied set) — a caller that derives
    /// its "emitted" list from the same constants class it checks against gets no protection
    /// against a NEW bare-literal emission site (proven by mutation: see
    /// <c>Affiant.Testing.ComplianceHarness.Tests.AssertToolErrorCodeRegistryParityTests</c>'s own
    /// remarks). Catching undeclared, drifting-forward emissions is a live source-scan's job (see
    /// <c>Affiant.Testing.ComplianceHarness.Tests.AssertToolErrorCodeSourceScanTests</c> for the
    /// framework's own) — this method remains the right tool for the orphan half of the
    /// contract.</para>
    /// </summary>
    /// <param name="toolErrorCodesType">
    /// A type whose <c>public const string</c> fields are the declared <c>ToolError.Code</c>
    /// registry (a <c>ToolErrorCodes</c>-style class).
    /// </param>
    /// <param name="emittedCodes">
    /// Every distinct <c>ToolError.Code</c> value actually emitted — caller-supplied (see remarks);
    /// duplicates are harmless (deduplicated internally).
    /// </param>
    /// <param name="exemptConstants">
    /// <paramref name="toolErrorCodesType"/> member names deliberately excluded from the orphan
    /// check (e.g. a code reserved for an emission site not yet wired up). Defaults to empty.
    /// </param>
    public static ToolErrorCodeParityResult AssertToolErrorCodeRegistryParity(
        Type toolErrorCodesType,
        IReadOnlyCollection<string> emittedCodes,
        IReadOnlyCollection<string>? exemptConstants = null)
    {
        ArgumentNullException.ThrowIfNull(toolErrorCodesType);
        ArgumentNullException.ThrowIfNull(emittedCodes);

        var exempt = new HashSet<string>(exemptConstants ?? [], StringComparer.Ordinal);
        var declared = GetConstStringMembers(toolErrorCodesType);
        var declaredValues = new HashSet<string>(declared.Values, StringComparer.Ordinal);
        var emitted = new HashSet<string>(emittedCodes, StringComparer.Ordinal);

        var orphanConstants = declared
            .Where(kv => !exempt.Contains(kv.Key) && !emitted.Contains(kv.Value))
            .Select(kv => new ParityViolation(
                kv.Key,
                $"{toolErrorCodesType.Name}.{kv.Key} (\"{kv.Value}\") is declared but does not match " +
                "any supplied emitted code — orphaned constant, or the emitted-code enumeration is stale."))
            .ToList();

        var undeclaredCodes = emitted
            .Where(code => !declaredValues.Contains(code))
            .Select(code => new ParityViolation(
                code,
                $"ToolError code \"{code}\" is emitted but is not the value of any " +
                $"{toolErrorCodesType.Name} constant — a bare literal escaped the registry."))
            .ToList();

        return new ToolErrorCodeParityResult(
            orphanConstants.Count == 0 && undeclaredCodes.Count == 0, orphanConstants, undeclaredCodes);
    }

    /// <summary>
    /// Reflects a type's <c>public const string</c> fields into a member-name→value map — the
    /// shared acquisition step for <see cref="AssertToolNameRegistryParity"/> and
    /// <see cref="AssertFabricKeyParity"/>'s <c>ToolNames</c>/<c>FabricKeys</c>-style constants
    /// classes. Pure reflection over the supplied type only — never over an adapter's own types —
    /// so it introduces no adapter-package dependency.
    /// </summary>
    private static IReadOnlyDictionary<string, string> GetConstStringMembers(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

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
            var resolvers = provider.GetServices<IFieldResolver>();
#pragma warning disable CS0618 // IDeterministicFieldSource is obsolete but kept fully functional — see type XML docs.
            var deterministicSources = provider.GetServices<IDeterministicFieldSource>();
#pragma warning restore CS0618
            var projection = new SchemaDrivenAffidavitProjection(
                strategy,
                resolvers,
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
