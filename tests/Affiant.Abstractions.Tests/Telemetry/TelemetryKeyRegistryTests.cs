namespace Affiant.Abstractions.Tests.Telemetry;

using System.Reflection;
using System.Text.Json;
using Affiant.Abstractions.Telemetry;
using Xunit;

/// <summary>
/// The registry is a versioned API (rulebook rule TL-1), so these tests guard it as one: the keys
/// never disappear, the shipped JSON document and the compiled constants never drift apart, and the
/// document keeps validating against the rulebook's own schema.
///
/// <para>
/// The rulebook's schema, its <c>common.schema.json</c> companion, and its two conformance fixtures
/// are vendored under <c>Telemetry/rulebook/</c>; that directory's <c>README.md</c> records the
/// upstream repository and the exact commit they were copied from.
/// </para>
/// </summary>
public class TelemetryKeyRegistryTests
{
    /// <summary>
    /// The nine v0.1 keys, written out here a second time on purpose.
    ///
    /// <para>
    /// This list is the snapshot that makes "a key is never removed" enforceable. Deriving it from
    /// <see cref="TelemetryKeys.All"/> would make the assertion circular — deleting a key would
    /// delete the evidence that it ever existed — so it is duplicated, and a deletion has to be
    /// made twice, in two files, to get past this suite. Adding a key is a one-line addition here;
    /// removing one should not be possible at all before a major version.
    /// </para>
    /// </summary>
    private static readonly string[] KeysThatMustNeverDisappear =
    [
        "affidavit.filed",
        "affidavit.refused.substance",
        "coverage.refused",
        "docket.transition",
        "docket.expired",
        "decision.unauthorized",
        "standing-order.fired",
        "standing-order.blocked",
        "policy.invalid",
    ];

    [Fact]
    public void EveryKeyEverShipped_IsStillInTheRegistry()
    {
        foreach (var key in KeysThatMustNeverDisappear)
        {
            Assert.True(
                TelemetryKeys.All.Contains(key),
                $"Telemetry key '{key}' has been removed from the registry. Operators build alerts on " +
                "these names: a key is deprecated, never removed (TL-1). If this key genuinely has to " +
                "go, that is a major-version change and this list is where it is argued.");
        }
    }

    [Fact]
    public void RegistryOrder_OnlyEverGrowsAtTheEnd()
    {
        Assert.Equal(KeysThatMustNeverDisappear, TelemetryKeys.All.Take(KeysThatMustNeverDisappear.Length));
    }

    [Fact]
    public void CompiledConstants_AndShippedDocument_NameTheSameKeysInTheSameOrder()
    {
        Assert.Equal(TelemetryKeys.All, TelemetryKeys.Registry.Keys.Select(k => k.Key));
    }

    [Fact]
    public void EveryKeyDeclares_WhenItFirstShipped_AndWhatItMeans()
    {
        foreach (var entry in TelemetryKeys.Registry.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Since), $"'{entry.Key}' declares no `since`.");
            Assert.False(string.IsNullOrWhiteSpace(entry.Description), $"'{entry.Key}' declares no description.");
        }
    }

    [Fact]
    public void EveryAttributeName_InTheDocument_HasAConstant()
    {
        var constants = typeof(TelemetryKeys.Attributes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var inDocument = TelemetryKeys.Registry.Keys.SelectMany(k => k.Attributes).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(inDocument.Except(constants));
    }

    [Fact]
    public void EveryAttributeConstant_IsCarriedByAtLeastOneKey()
    {
        var constants = typeof(TelemetryKeys.Attributes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var inDocument = TelemetryKeys.Registry.Keys.SelectMany(k => k.Attributes).ToHashSet(StringComparer.Ordinal);

        // gen_ai.operation.name is the exception: TL-2 fixes the spelling for the operation name, and
        // the constant exists so a call site cannot invent a second spelling, but no v0.1 key carries
        // it yet. Every other constant must be reachable from the document, or it is a typo nobody
        // would ever notice.
        Assert.Equal(
            [TelemetryKeys.Attributes.GenAiOperationName],
            constants.Except(inDocument).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// TL-2: where a public standard names the same thing, the standard's name is used. These three
    /// are OpenTelemetry's <c>gen_ai.*</c> semantic-convention attributes, spelled exactly.
    /// </summary>
    [Fact]
    public void StandardsVocabulary_IsSpelledTheStandardsWay()
    {
        Assert.Equal("gen_ai.tool.name", TelemetryKeys.Attributes.GenAiToolName);
        Assert.Equal("gen_ai.conversation.id", TelemetryKeys.Attributes.GenAiConversationId);
        Assert.Equal("gen_ai.operation.name", TelemetryKeys.Attributes.GenAiOperationName);

        // Nothing in the registry may carry a field VALUE. The nearest thing to a leak is an
        // attribute whose name says "value"; the rule is stated in TelemetryKeys' own docs, and this
        // is the cheap mechanical half of it.
        foreach (var attribute in TelemetryKeys.Registry.Keys.SelectMany(k => k.Attributes))
            Assert.DoesNotContain("value", attribute, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedRegistry_ValidatesAgainstTheRulebookSchema()
    {
        using var registry = JsonDocument.Parse(ReadEmbeddedRegistry());
        using var schema = JsonDocument.Parse(ReadRulebookFile("telemetry-key.schema.json"));
        using var common = JsonDocument.Parse(ReadRulebookFile("common.schema.json"));

        var violations = JsonSchemaChecker.Validate(
            registry.RootElement,
            schema.RootElement,
            new Dictionary<string, JsonElement> { ["common.schema.json"] = common.RootElement });

        Assert.Empty(violations);
    }

    /// <summary>
    /// The validator itself is checked against the rulebook's own fixtures: its positive fixture
    /// must pass and its negative fixture must fail. Without this, a validator that returned no
    /// violations for anything would make the test above pass forever.
    /// </summary>
    [Theory]
    [InlineData("fixture-01-registry.json", true)]
    [InlineData("fixture-90-key-without-attributes.json", false)]
    public void SchemaChecker_AgreesWithTheRulebooksOwnFixtures(string fixture, bool expectedValid)
    {
        using var instance = JsonDocument.Parse(ReadRulebookFile(fixture));
        using var schema = JsonDocument.Parse(ReadRulebookFile("telemetry-key.schema.json"));
        using var common = JsonDocument.Parse(ReadRulebookFile("common.schema.json"));

        var violations = JsonSchemaChecker.Validate(
            instance.RootElement,
            schema.RootElement,
            new Dictionary<string, JsonElement> { ["common.schema.json"] = common.RootElement });

        Assert.Equal(expectedValid, violations.Count == 0);
    }

    [Fact]
    public void RegistryDocument_IsEmbeddedUnderTheNameTheConstantPromises()
    {
        Assert.Contains(
            TelemetryKeys.RegistryResourceName,
            typeof(TelemetryKeys).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void Contains_AnswersForRegistryKeysAndOnlyThoseKeys()
    {
        Assert.True(TelemetryKeys.Contains(TelemetryKeys.DocketTransition));
        Assert.False(TelemetryKeys.Contains("affidavit.projected"));
        Assert.False(TelemetryKeys.Contains(""));
    }

    private static byte[] ReadEmbeddedRegistry()
    {
        using var stream = typeof(TelemetryKeys).Assembly
            .GetManifestResourceStream(TelemetryKeys.RegistryResourceName)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] ReadRulebookFile(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Telemetry", "rulebook", fileName));
}
