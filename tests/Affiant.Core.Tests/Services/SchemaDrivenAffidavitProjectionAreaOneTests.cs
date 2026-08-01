namespace Affiant.Core.Tests.Services;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Area-1 field-provenance redesign (chancery docs/architecture-review/area-1-field-provenance-model.md):
/// P1 extraction fields (<see cref="TaskInferenceField.Projected"/>) and P2 resolver precedence +
/// chain-merge semantics (the "V4" fix). Kept in its own file rather than growing
/// <see cref="SchemaDrivenAffidavitProjectionTests"/> further.
/// </summary>
#pragma warning disable CS0618 // Legacy IDeterministicFieldSource path exercised deliberately (kept-working + merge fix).
public class SchemaDrivenAffidavitProjectionAreaOneTests
{
    // --- Fakes ---

    private sealed class MixedStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Color", "string", "Color of the widget"),
            new("TailNumber", "string", "Tail number mentioned in conversation", Projected: false),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class TwoFieldStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Color", "string", "Color of the widget"),
            new("Weight", "string", "Weight in kg"),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class InvalidExtractionRequiredStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Widget";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Bad", "string", "invalid combo", Required: true, Projected: false),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class RegistrationFromTailStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Aircraft";
        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("Registration", "string", "Registration derived from the tail number"),
            new("TailNumber", "string", "Tail number mentioned in conversation", Projected: false),
        ];
        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class FixedSource : IDeterministicFieldSource
    {
        private readonly ProvenanceTag? _tag;
        public string FieldName { get; }
        public FixedSource(string fieldName, ProvenanceTag? tag) { FieldName = fieldName; _tag = tag; }
        public ProvenanceTag? Resolve(IContextFabric fabric) => _tag;
    }

    private sealed class FixedResolver : IFieldResolver
    {
        private readonly object? _value;
        private readonly ProvenanceTag? _tag;
        public string FieldName { get; }
        public FixedResolver(string fieldName, object? value, ProvenanceTag? tag) { FieldName = fieldName; _value = value; _tag = tag; }
        public Task<FieldResolution?> ResolveAsync(FieldResolutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(_tag is null ? null : new FieldResolution(_value, _tag));
    }

    private sealed class TailNumberDerivedResolver : IFieldResolver
    {
        public string FieldName => "Registration";

        public Task<FieldResolution?> ResolveAsync(FieldResolutionContext context, CancellationToken cancellationToken)
        {
            if (!context.Facts.TryGetValue("TailNumber", out var fact) || fact.Value is not string tail)
                return Task.FromResult<FieldResolution?>(null);

            return Task.FromResult<FieldResolution?>(new FieldResolution(
                tail,
                new ProvenanceTag(ProvenanceSource.Computed, 0.95f, $"Resolved from tail number {tail} (stated in conversation)", null)));
        }
    }

    private static SchemaDrivenAffidavitProjection BuildProjection(
        ITaskInferenceStrategy? strategy = null,
        IEnumerable<IFieldResolver>? resolvers = null,
        IEnumerable<IDeterministicFieldSource>? sources = null)
    {
        strategy ??= new TwoFieldStrategy();
        resolvers ??= [];
        sources ??= [];
        return new SchemaDrivenAffidavitProjection(
            strategy, resolvers, sources, NullLogger<SchemaDrivenAffidavitProjection>.Instance,
            new InMemoryObservabilityEventStream<AffidavitEmittedEvent>());
    }

    // --- P1: Projected=false fields never become AffidavitField ---

    [Fact]
    public void Project_ExcludesNonProjectedFields()
    {
        var fabric = new ContextFabric();
        fabric.SetFieldChain("TailNumber", ProvenanceChain.From(ProvenanceTag.FromInference("TailNumber", 0.8f)));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object>
        {
            ["TailNumber"] = "N12345",
        }));

        var projection = BuildProjection(new MixedStrategy());
        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var field = Assert.Single(affidavit.Fields);
        Assert.Equal("Color", field.Name);
        Assert.DoesNotContain(affidavit.Fields, f => f.Name == "TailNumber");
    }

    // --- P1: Projected=false + Required=true is rejected loudly at construction ---

    [Fact]
    public void Constructor_ProjectedFalseAndRequiredTrue_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => BuildProjection(new InvalidExtractionRequiredStrategy()));

        Assert.Contains("Bad", ex.Message);
        Assert.Contains("Projected=false", ex.Message);
        Assert.Contains("Required=true", ex.Message);
    }

    // --- P2: ExtractionFacts reach resolvers ---

    [Fact]
    public void ExtractionFacts_ReachResolvers()
    {
        var fabric = new ContextFabric();
        fabric.SetFieldChain("TailNumber", ProvenanceChain.From(ProvenanceTag.FromInference("TailNumber", 0.7f)));
        fabric.Upsert(new EntityRef("Aircraft", "Aircraft", "Aircraft", new Dictionary<string, object>
        {
            ["TailNumber"] = "N12345",
        }));

        var projection = BuildProjection(
            new RegistrationFromTailStrategy(),
            resolvers: [new TailNumberDerivedResolver()]);

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var registration = affidavit.Fields.Single(f => f.Name == "Registration");
        Assert.Equal("N12345", registration.Value);
        Assert.Equal(ProvenanceSource.Computed, registration.Provenance.Current.Source);
        Assert.Contains("N12345", registration.Provenance.Current.Evidence);
        Assert.DoesNotContain(affidavit.Fields, f => f.Name == "TailNumber");
    }

    [Fact]
    public void ExtractionFacts_AbsentWhenFabricHasNoChain()
    {
        // No fabric state at all for TailNumber → the resolver sees no fact and returns null →
        // falls through to Empty (no legacy source, no fabric chain for "Registration" either).
        var fabric = new ContextFabric();

        var projection = BuildProjection(
            new RegistrationFromTailStrategy(),
            resolvers: [new TailNumberDerivedResolver()]);

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var registration = affidavit.Fields.Single(f => f.Name == "Registration");
        Assert.Null(registration.Value);
        Assert.Equal(ProvenanceSource.Empty, registration.Provenance.Current.Source);
    }

    // --- P2: precedence — resolver wins over legacy source ---

    [Fact]
    public void Resolver_TakesPrecedenceOverLegacySource()
    {
        var fabric = new ContextFabric();
        var resolver = new FixedResolver("Color", "Purple", new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, "via-resolver", null));
        var legacySource = new FixedSource("Color", new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, "via-legacy", null));

        var projection = BuildProjection(resolvers: [resolver], sources: [legacySource]);
        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal("Purple", colorField.Value);
        Assert.Equal("via-resolver", colorField.Provenance.Current.Evidence);
    }

    // --- P2: precedence — resolver returning null falls back to legacy source ---

    [Fact]
    public void Resolver_ReturnsNull_FallsBackToLegacySource()
    {
        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object> { ["Color"] = "Green" }));
        var deterministicTag = ProvenanceTag.FromUser("Color");

        var projection = BuildProjection(
            resolvers: [new FixedResolver("Color", null, null)],
            sources: [new FixedSource("Color", deterministicTag)]);

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal(ProvenanceSource.UserStated, colorField.Provenance.Current.Source);
        Assert.Equal("Green", colorField.Value);
    }

    // --- P2 / V4 fix: resolver tag MERGES onto a pre-existing chain rather than truncating it ---

    [Fact]
    public void Resolver_MergesOntoExistingChain_CurrentIsResolverTag_PriorContainsConversationTag()
    {
        var fabric = new ContextFabric();
        var conversationTag = new ProvenanceTag(ProvenanceSource.Conversation, 0.5f, "Mentioned in conversation", null);
        fabric.SetFieldChain("Color", ProvenanceChain.From(conversationTag));

        var resolverTag = new ProvenanceTag(ProvenanceSource.UserStated, 1.0f, "User confirmed color", null);
        var resolver = new FixedResolver("Color", "Red", resolverTag);

        var projection = BuildProjection(resolvers: [resolver]);
        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal(resolverTag, colorField.Provenance.Current);
        Assert.Contains(conversationTag, colorField.Provenance.Prior);
        Assert.Equal("Red", colorField.Value);
    }

    // --- P2 / V4 fix: legacy source path ALSO merges (not just From-truncate) ---

    [Fact]
    public void LegacySource_MergesOntoExistingChain_PreservesPriorHistory()
    {
        var fabric = new ContextFabric();
        var conversationTag = new ProvenanceTag(ProvenanceSource.Conversation, 0.5f, "Mentioned in conversation", null);
        fabric.SetFieldChain("Color", ProvenanceChain.From(conversationTag));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object> { ["Color"] = "Blue" }));

        var deterministicTag = ProvenanceTag.FromUser("Color");
        var projection = BuildProjection(sources: [new FixedSource("Color", deterministicTag)]);

        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal(deterministicTag, colorField.Provenance.Current);
        Assert.Contains(conversationTag, colorField.Provenance.Prior);
        Assert.Equal("Blue", colorField.Value);
    }

    // --- V4 fix, no-prior case: result stays structurally identical to today's From(tag) ---

    [Fact]
    public void LegacySource_NoExistingChain_ResultIdenticalToFromTag()
    {
        var fabric = new ContextFabric();
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object> { ["Color"] = "Blue" }));
        var deterministicTag = ProvenanceTag.FromUser("Color");

        var projection = BuildProjection(sources: [new FixedSource("Color", deterministicTag)]);
        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal(ProvenanceChain.From(deterministicTag), colorField.Provenance);
    }

    // --- Merge, losing-candidate branch (SchemaDrivenAffidavitProjection.cs's candidateWins == false
    // path): every test above uses a candidate whose confidence exceeds the existing chain's, so
    // candidateWins is always true and the "existing chain wins" branch has never been exercised.
    // These two lock it down for both ladder rungs (resolver, legacy source).

    [Fact]
    public void Resolver_LosingCandidate_CurrentStaysExisting_PriorGainsResolverTag_ValueReflectsExistingEntity()
    {
        var fabric = new ContextFabric();
        var existingTag = ProvenanceTag.FromUser("Color"); // UserStated, confidence 1.0
        fabric.SetFieldChain("Color", ProvenanceChain.From(existingTag));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object> { ["Color"] = "Blue" }));

        // Resolver tag is lower-confidence than the existing UserStated 1.0 tag, so it must lose the merge.
        var losingTag = new ProvenanceTag(ProvenanceSource.Computed, 0.4f, "low-confidence resolver guess", null);
        var resolver = new FixedResolver("Color", "GreenFromResolver", losingTag);

        var projection = BuildProjection(resolvers: [resolver]);
        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal(existingTag, colorField.Provenance.Current);
        Assert.Contains(losingTag, colorField.Provenance.Prior);
        // The projected Value must reflect the existing (winning) entity value, never the losing
        // resolver's computed Value — a regression here would silently surface an unconfirmed,
        // lower-confidence guess on the Evidence Card ahead of a UserStated fact.
        Assert.Equal("Blue", colorField.Value);
        Assert.NotEqual("GreenFromResolver", colorField.Value);
    }

    [Fact]
    public void LegacySource_LosingCandidate_CurrentStaysExisting_PriorGainsLegacyTag_ValueReflectsExistingEntity()
    {
        var fabric = new ContextFabric();
        var existingTag = ProvenanceTag.FromUser("Color"); // UserStated, confidence 1.0
        fabric.SetFieldChain("Color", ProvenanceChain.From(existingTag));
        fabric.Upsert(new EntityRef("Widget", "Widget", "Widget", new Dictionary<string, object> { ["Color"] = "Blue" }));

        // Legacy source tag is lower-confidence than the existing UserStated 1.0 tag, so it must lose.
        var losingTag = new ProvenanceTag(ProvenanceSource.Computed, 0.4f, "low-confidence legacy guess", null);

        var projection = BuildProjection(sources: [new FixedSource("Color", losingTag)]);
        var affidavit = projection.Project(fabric, "WriteCreate", []);

        var colorField = affidavit.Fields.Single(f => f.Name == "Color");
        Assert.Equal(existingTag, colorField.Provenance.Current);
        Assert.Contains(losingTag, colorField.Provenance.Prior);
        Assert.Equal("Blue", colorField.Value);
    }
}
#pragma warning restore CS0618
