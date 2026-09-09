namespace Affiant.Core.Tests.Filters;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// PV-3's condition is "the value is literally present in the utterance", and these are the facts
/// about the utterance the step now establishes for itself: which values are found, where, what the
/// binding says, and what a port's own claim is worth against the text (Sakwala/affiant#123).
/// </summary>
public class TaskInferenceStepPresenceTests
{
    /// <summary>The seven fields of the work-order sentence the defect was reported against.</summary>
    private sealed class WorkOrderStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "WorkOrder";

        public IReadOnlyList<TaskInferenceField> Fields { get; } =
        [
            new("AircraftId", "string", "The tail number"),
            new("Title", "string", "What is wrong"),
            new("Priority", "string", "How urgent"),
            new("EstimatedHours", "number", "How long"),
            new("AssignedTo", "string", "Who does it"),
            new("DueDate", "string", "When it is due"),
            new("Type", "string", "The kind of order"),
        ];

        public double? MinimumConfidenceThreshold => null;
    }

    private sealed class OneFieldStrategy : ITaskInferenceStrategy
    {
        public string EntityName => "Thing";

        public IReadOnlyList<TaskInferenceField> Fields { get; } =
            [new("Field", "string", "The field")];

        public double? MinimumConfidenceThreshold => null;
    }

    private const string MeridianUtterance =
        "Create an AOG work order for WZ-BRN. Title: Left engine oil pressure fluctuation. " +
        "Priority Critical, estimated 6 hours, assign it to Rajesh Kumar, due 2026-09-08";

    private static (ContextFabric Fabric, TaskInferenceStep Step) Build()
    {
        var fabric = new ContextFabric();
        return (fabric, new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance));
    }

    private static JsonElement Reported(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>One field, reported the way every shipped port reports: value and confidence only.</summary>
    private static JsonElement Silent(string valueJson) =>
        Reported($$"""{ "Field": { "value": {{valueJson}}, "confidence": 0.6 } }""");

    private static string Sha256OfUtf8(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static UtteranceSpanRef SpanOf(ProvenanceTag tag) =>
        Assert.IsType<ProvenanceBinding.UtteranceSpan>(tag.Binding).Ref;

    /// <summary>
    /// The sentence from the defect report: seven values that are in it, and a port that says nothing
    /// about presence because no shipped port is asked to. Every one is graded from the turn, and
    /// every span slices back to the value it was minted for.
    /// </summary>
    [Fact]
    public async Task TheSevenValuesInTheMeridianSentence_AreConversation_AndEverySpanSlicesBack()
    {
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "AircraftId":     { "value": "WZ-BRN", "confidence": 0.9 },
              "Title":          { "value": "Left engine oil pressure fluctuation", "confidence": 0.9 },
              "Priority":       { "value": "Critical", "confidence": 0.9 },
              "EstimatedHours": { "value": 6, "confidence": 0.9 },
              "AssignedTo":     { "value": "Rajesh Kumar", "confidence": 0.9 },
              "DueDate":        { "value": "2026-09-08", "confidence": 0.9 },
              "Type":           { "value": "AOG", "confidence": 0.9 }
            }
            """);

        await step.ExecuteAsync(new WorkOrderStrategy(), reported, MeridianUtterance, default);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AircraftId"] = "WZ-BRN",
            ["Title"] = "Left engine oil pressure fluctuation",
            ["Priority"] = "Critical",
            ["EstimatedHours"] = "6",
            ["AssignedTo"] = "Rajesh Kumar",
            ["DueDate"] = "2026-09-08",
            ["Type"] = "AOG",
        };

        foreach (var (field, text) in expected)
        {
            var tag = fabric.GetFieldChain(field)!.Current;
            Assert.Equal(ProvenanceSource.Conversation, tag.Source);

            var span = SpanOf(tag);
            Assert.Equal(text, MeridianUtterance.Substring(span.Offset, span.Length));
            Assert.Equal(Sha256OfUtf8(text), span.Hash);
        }
    }

    /// <summary>
    /// The digest is over the utterance's own bytes, not over the text the port reported, so a value
    /// that differs from the turn only in case hits and is bound to what the turn actually says.
    /// </summary>
    [Fact]
    public async Task AValueDifferingOnlyInCase_Hits_AndTheDigestIsTheUtterancesOwn()
    {
        const string utterance = "Book the client lunch for Tuesday";
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Client Lunch\""), utterance, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);

        var span = SpanOf(tag);
        Assert.Equal("client lunch", utterance.Substring(span.Offset, span.Length));
        Assert.Equal(Sha256OfUtf8("client lunch"), span.Hash);
        Assert.NotEqual(Sha256OfUtf8("Client Lunch"), span.Hash);
    }

    /// <summary>
    /// A hit's neighbours must be neither letters nor digits, so the 20 a model reasoned to is not
    /// found inside the year it happens to start.
    /// </summary>
    [Fact]
    public async Task AValueOnlyInsideALongerNumber_DoesNotHit()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("20"), "estimated 6 hours, due 2026-09-08", default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    /// <summary>The same rule on letters: a word inside a longer word is not that word.</summary>
    [Fact]
    public async Task AValueOnlyInsideALongerWord_DoesNotHit()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"cat\""), "the catalogue is ready", default);

        Assert.Equal(ProvenanceSource.Inferred, fabric.GetFieldChain("Field")!.Current.Source);
    }

    /// <summary>
    /// The case #123 is about: the port reports value and confidence and nothing else — which is
    /// what all three shipped ports do — and the value is in the turn. The framework finds it.
    /// </summary>
    [Fact]
    public async Task ASilentPort_AndAValueInTheTurn_IsConversation()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Critical\""), "Priority Critical please", default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(9, SpanOf(tag).Offset);
    }

    /// <summary>A claim the text does not confirm buys nothing: the port does not decide the grade.</summary>
    [Fact]
    public async Task APortClaimingLiteral_ForAValueThatIsNotInTheTurn_IsInferred()
    {
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": "Critical",
                "confidence": 0.6,
                "presence": "literal",
                "utteranceSpan": { "start": 0, "end": 8 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, "make this one urgent", default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    /// <summary>
    /// A span that verifies is used, and it beats the first hit: the port knows which occurrence it
    /// read, and the framework checks that the utterance there says what the port said it says.
    /// </summary>
    [Fact]
    public async Task APortSpanThatVerifies_IsUsed_EvenWhenAnEarlierOccurrenceExists()
    {
        const string utterance = "Active now, and Active later";
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": "Active",
                "confidence": 0.6,
                "utteranceSpan": { "start": 16, "end": 22 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, utterance, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(16, SpanOf(tag).Offset);
    }

    /// <summary>A span that does not verify is discarded, and the first hit stands.</summary>
    [Fact]
    public async Task APortSpanThatDoesNotVerify_FallsBackToTheFirstHit()
    {
        const string utterance = "Active now, and Active later";
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": "Active",
                "confidence": 0.6,
                "utteranceSpan": { "start": 400, "end": 406 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, utterance, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(0, SpanOf(tag).Offset);
    }

    /// <summary>
    /// D4: a caller with no turn in hand keeps beta.3's behaviour — the port's report is all there
    /// is, and it stands, digest over the value the port reported.
    /// </summary>
    [Fact]
    public async Task WithNoUtterance_ThePortsReportStands()
    {
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": "Critical",
                "confidence": 0.6,
                "presence": "literal",
                "utteranceSpan": { "start": 9, "end": 17 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);

        var span = SpanOf(tag);
        Assert.Equal(9, span.Offset);
        Assert.Equal(8, span.Length);
        Assert.Equal(Sha256OfUtf8("Critical"), span.Hash);
    }

    /// <summary>An empty turn is a turn with nothing in it: nothing is present in it.</summary>
    [Fact]
    public async Task AnEmptyUtterance_FindsNothing()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Critical\""), string.Empty, default);

        Assert.Equal(ProvenanceSource.Inferred, fabric.GetFieldChain("Field")!.Current.Source);
    }

    /// <summary>A value that is the whole turn is present in it, bounded by both ends of the string.</summary>
    [Fact]
    public async Task AValueEqualToTheWholeUtterance_Hits()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Critical\""), "Critical", default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(0, SpanOf(tag).Offset);
        Assert.Equal(8, SpanOf(tag).Length);
    }

    /// <summary>
    /// A3: a number's value text is its SR-1 canonical rendering, not the raw JSON token. A port
    /// that hands its runtime a parsed number has already lost the token — a JavaScript
    /// implementation reading the same report sees 6 where the wire said 6.0 — so the rule that
    /// both implementations can apply is the canonical one. The person typed 6, and 6 is what the
    /// record swears to.
    /// </summary>
    [Theory]
    [InlineData("6.0", "estimated 6 hours", 10, "6")]
    [InlineData("6.00", "estimated 6 hours", 10, "6")]
    [InlineData("1e2", "estimated 100 hours", 10, "100")]
    [InlineData("6.50", "estimated 6.5 hours", 10, "6.5")]
    [InlineData("-0", "0 hours left", 0, "0")]
    public async Task ANumbersValueTextIsItsCanonicalRendering(
        string reportedNumber, string utterance, int offset, string sliced)
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent(reportedNumber), utterance, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(offset, SpanOf(tag).Offset);
        Assert.Equal(sliced, utterance.Substring(SpanOf(tag).Offset, SpanOf(tag).Length));
    }

    /// <summary>A3: a boolean's value text is <c>true</c> or <c>false</c>, and it is found like any other.</summary>
    [Fact]
    public async Task ABooleansValueTextIsTrueOrFalse()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("true"), "set grounded to true please", default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(16, SpanOf(tag).Offset);
    }

    /// <summary>
    /// A3: JSON null, an object and an array have no value text and never hit — they are skipped
    /// before the finder is reached, as they were before this rule existed.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{ \"a\": 1 }")]
    [InlineData("[1, 2]")]
    public async Task AValueWithNoValueText_IsSkipped(string valueJson)
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent(valueJson), "a 1 2 null here", default);

        Assert.Null(fabric.GetFieldChain("Field"));
    }

    /// <summary>Offsets are UTF-16 code units, which is what the binding records.</summary>
    [Fact]
    public async Task OffsetsAreUtf16CodeUnits_PastANonBmpCharacter()
    {
        const string utterance = "\U0001F681 grounded: Critical";
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Critical\""), utterance, default);

        var span = SpanOf(fabric.GetFieldChain("Field")!.Current);
        Assert.Equal("Critical", utterance.Substring(span.Offset, span.Length));
        Assert.Equal(13, span.Offset);
    }

    /// <summary>A turn is what the person typed, newlines included; a hit may be on any line of it.</summary>
    [Fact]
    public async Task AMultiLineUtterance_IsSearchedWhole()
    {
        const string utterance = "Work order for WZ-BRN\nPriority: Critical";
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Critical\""), utterance, default);

        var span = SpanOf(fabric.GetFieldChain("Field")!.Current);
        Assert.Equal("Critical", utterance.Substring(span.Offset, span.Length));
    }

    // --- A12: the fold is ASCII-only (design record §6) ---

    /// <summary>
    /// A12: the fold is ASCII, so no runtime case table is consulted at all — and every reading of a
    /// non-ASCII case pair a table might have given is refused here, in one list. <c>ß</c> against
    /// the capital sharp s (a full mapping would make it <c>SS</c>); the Turkish dotless and dotted
    /// i in both directions (<c>UnicodeData.txt</c> maps ı to I, .NET's invariant table does not,
    /// and JavaScript's <c>toUpperCase</c> does); the Kelvin sign against a plain K; and the fi
    /// ligature against the letters it draws. Each is <c>Inferred</c>, unbound, in both
    /// implementations, because neither implementation has a table to disagree about.
    /// </summary>
    [Theory]
    [InlineData("\"Straße\"", "Adresse STRAẞE 5")]
    [InlineData("\"bakım\"", "proje BAKIM listesi")]
    [InlineData("\"BAKIM\"", "proje bakım listesi")]
    [InlineData("\"kapalı\"", "durum KAPALI bugün")]
    [InlineData("\"KAPALI\"", "durum kapalı bugün")]
    [InlineData("\"K\"", "unit \u212A here")]
    [InlineData("\"fi\"", "the \uFB01le")]
    public async Task ANonAsciiCaseVariant_DoesNotHit(string reportedValue, string utterance)
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent(reportedValue), utterance, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    /// <summary>
    /// A12: what the ASCII fold still buys, and the whole reason the fold exists — the case fixture
    /// the rulebook pins. <c>Client Lunch</c> hits a typed <c>client lunch</c>, and an exact echo in
    /// any script hits whatever the case.
    /// </summary>
    [Theory]
    [InlineData("\"Client Lunch\"", "File the expense for client lunch, 40 EUR", 21)]
    [InlineData("\"kapalı\"", "durum kapalı bugün", 6)]
    public async Task AnAsciiCaseVariantOrAnExactEcho_Hits(string reportedValue, string utterance, int offset)
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent(reportedValue), utterance, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(offset, SpanOf(tag).Offset);
    }

    /// <summary>
    /// A12: no culture is consulted and nothing is normalised, so a dotted capital I earlier in the
    /// turn neither folds away nor shifts the offsets the binding records — they stay UTF-16 code
    /// units of the utterance as typed.
    /// </summary>
    [Fact]
    public async Task ADottedCapitalIEarlierInTheTurn_DoesNotDisturbTheOffsets()
    {
        const string utterance = "İstanbul ofis";
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"ofis\""), utterance, default);

        var span = SpanOf(fabric.GetFieldChain("Field")!.Current);
        Assert.Equal(9, span.Offset);
        Assert.Equal("ofis", utterance.Substring(span.Offset, span.Length));
    }

    // --- A2: the boundary categories are L*, M*, Nd, Pc (design record §6) ---

    /// <summary>
    /// A2: a combining mark blocks a hit. The grapheme the utterance draws is <c>é</c>, so the word
    /// there is <c>Café</c> and not the <c>Cafe</c> the value spells — and a binding into it would
    /// hash four code units of a five-code-unit character.
    /// </summary>
    [Fact]
    public async Task ACombiningMarkAfterTheMatch_BlocksTheHit()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Cafe\""), "Client cafe\u0301 lunch", default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    /// <summary>A2: connector punctuation joins an identifier, so <c>WZ</c> is not a word in <c>WZ_BRN</c>.</summary>
    [Fact]
    public async Task ConnectorPunctuationAfterTheMatch_BlocksTheHit()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"WZ\""), "order for WZ_BRN", default);

        Assert.Equal(ProvenanceSource.Inferred, fabric.GetFieldChain("Field")!.Current.Source);
    }

    // --- A4: a port span is used only when it is itself a hit (design record §6) ---

    /// <summary>
    /// A4: the span the refuter minted a <c>Conversation</c> from. The utterance at <c>{4, 6}</c>
    /// does say <c>20</c>, but its right neighbour is a digit, so it is not a hit and the port's
    /// claim does not beat the text. The finder then runs and finds nothing.
    /// </summary>
    [Fact]
    public async Task APortSpanIntoTheMiddleOfANumber_IsNotAHit()
    {
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": 20,
                "confidence": 0.6,
                "presence": "literal",
                "utteranceSpan": { "start": 4, "end": 6 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, "due 2026-09-08", default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    /// <summary>
    /// A4, the sharper form: a span that is not a hit never beats a genuine occurrence elsewhere in
    /// the same turn. The finder runs from the start and binds the standalone <c>20</c>.
    /// </summary>
    [Fact]
    public async Task APortSpanThatIsNotAHit_LosesToTheGenuineOccurrence()
    {
        const string utterance = "Room 20 in 2026";
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": 20,
                "confidence": 0.6,
                "utteranceSpan": { "start": 11, "end": 13 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, utterance, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(5, SpanOf(tag).Offset);
        Assert.Equal("20", utterance.Substring(SpanOf(tag).Offset, SpanOf(tag).Length));
    }

    // --- A5: "no utterance" means null, and nothing else (design record §6) ---

    /// <summary>
    /// A5: an empty turn is a turn. The finder stays in charge, finds nothing, and the port's
    /// unverifiable <c>literal</c> and span buy nothing.
    /// </summary>
    [Fact]
    public async Task AnEmptyUtterance_IsAnUtterance_SoAPortsLiteralClaimStillFails()
    {
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": "Critical",
                "confidence": 0.6,
                "presence": "literal",
                "utteranceSpan": { "start": 0, "end": 8 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, string.Empty, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    // --- A10: one span shape, one spelling of `literal` (design record §6) ---

    /// <summary>
    /// A10: the hint has the schema's shape and no other. <c>{ start, length }</c> is a shape the
    /// rulebook does not name and the sibling implementation does not read, so it is no hint at all
    /// and the finder runs — which here finds nothing, rather than binding the <c>20</c> inside
    /// <c>2026</c> the discarded hint pointed at.
    /// </summary>
    [Fact]
    public async Task ASpanHintInAShapeTheSchemaDoesNotName_IsNotRead()
    {
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": 20,
                "confidence": 0.6,
                "utteranceSpan": { "start": 4, "length": 2 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, "due 2026-09-08", default);

        Assert.Equal(ProvenanceSource.Inferred, fabric.GetFieldChain("Field")!.Current.Source);
    }

    /// <summary>A10: a hint with a start and no end is the same — not a span the schema names.</summary>
    [Fact]
    public async Task ASpanHintWithNoEnd_IsNotRead()
    {
        const string utterance = "6 aircraft, estimated 6 hours";
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": 6,
                "confidence": 0.6,
                "utteranceSpan": { "start": 22 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, utterance, default);

        Assert.Equal(0, SpanOf(fabric.GetFieldChain("Field")!.Current).Offset);
    }

    /// <summary>
    /// A10: on the no-turn path <c>presence</c> is matched against the rulebook's own token exactly.
    /// The enum is lowercase; <c>"LITERAL"</c> is not it, and a sibling implementation comparing
    /// strictly would not match it either.
    /// </summary>
    [Fact]
    public async Task WithNoUtterance_APresenceInAnotherCasing_IsNotTheRulebooksToken()
    {
        var (fabric, step) = Build();
        var reported = Reported("""
            { "Field": { "value": "WZ-BRN", "confidence": 0.6, "presence": "LITERAL" } }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported);

        Assert.Equal(ProvenanceSource.Inferred, fabric.GetFieldChain("Field")!.Current.Source);
    }

    /// <summary>
    /// On the no-turn path a reported offset below zero names no span, so it is filed as no binding
    /// rather than as a binding no reader could check.
    /// </summary>
    [Fact]
    public async Task WithNoUtterance_ASpanBelowZero_MintsNoBinding()
    {
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": "Critical",
                "confidence": 0.6,
                "presence": "literal",
                "utteranceSpan": { "start": -5, "end": 895 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Null(tag.Binding);
    }

    /// <summary>
    /// A10: the no-turn path is a state a shipped bridge can only reach by handing over a history
    /// with nothing a person said in it, so the step says at Debug that it took it — the grade is
    /// then the port's word, unverified, and an operator reading the log can see that it was.
    /// </summary>
    [Fact]
    public async Task WithNoUtterance_TheStepSaysSoAtDebug()
    {
        var fabric = new ContextFabric();
        var logger = new CapturingLogger<TaskInferenceStep>();
        var step = new TaskInferenceStep(fabric, logger);

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Critical\""));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("no turn in hand", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("Field", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>With a turn in hand there is nothing to say: the finder decided, not the port.</summary>
    [Fact]
    public async Task WithATurn_TheStepDoesNotSayItHadNone()
    {
        var fabric = new ContextFabric();
        var logger = new CapturingLogger<TaskInferenceStep>();
        var step = new TaskInferenceStep(fabric, logger);

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"Critical\""), "Priority Critical please", default);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("no turn in hand", StringComparison.Ordinal));
    }

    // --- A15: the value text is the canonical rendering of the value the step files ---

    /// <summary>
    /// A15: the finder never searches for a number other than the one on the Affidavit. An integer
    /// outside the range a double holds exactly is filed exactly, so it is searched for exactly —
    /// reading the token back as a double would look for the rounded neighbour and, finding it, bind
    /// the record to text that is not the value (PV-2).
    /// </summary>
    [Fact]
    public async Task ALargeIntegerIsSearchedForAsTheIntegerItIsFiledAs()
    {
        const string utterance = "reference 9007199254740992 please";
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("9007199254740993"), utterance, default);

        var chain = fabric.GetFieldChain("Field")!;
        Assert.Equal(ProvenanceSource.Inferred, chain.Current.Source);
        Assert.Null(chain.Current.Binding);
        Assert.Equal(9007199254740993L, fabric.GetByKey("Thing")!.Fields["Field"]);
    }

    /// <summary>
    /// A15, the other direction: the same integer typed is found, and the span slices back to the
    /// digits the record swears to.
    /// </summary>
    [Fact]
    public async Task ALargeIntegerThatIsTypedIsFound_AndTheSpanIsTheValue()
    {
        const string utterance = "reference 9007199254740993 please";
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("9007199254740993"), utterance, default);

        var span = SpanOf(fabric.GetFieldChain("Field")!.Current);
        Assert.Equal("9007199254740993", utterance.Substring(span.Offset, span.Length));
    }

    // --- A17: a value a field cannot carry is nothing reported ---

    /// <summary>
    /// A17: the empty string is not a value a field can carry, so the port reported nothing for that
    /// field: nothing is merged, no tag is minted, and the field stays whatever it was — <c>Empty</c>
    /// under AF-1 when nothing else set it. A confidence is not a substitute for a value.
    /// </summary>
    [Fact]
    public async Task AnEmptyStringIsNothingReported()
    {
        var (fabric, step) = Build();

        var result = await step.ExecuteAsync(
            new OneFieldStrategy(), Silent("\"\""), "there is nothing to say here", default);

        Assert.Null(fabric.GetFieldChain("Field"));
        Assert.Empty(result.MergedFields);
    }

    /// <summary>
    /// A17: a whitespace-only string IS a value, and is filed as the port reported it. It never
    /// hits — whitespace-only value text is not searched for — so it is <c>Inferred</c> and unbound
    /// even over a turn full of spaces.
    /// </summary>
    [Fact]
    public async Task AWhitespaceOnlyStringIsAValue_FiledAsReported()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"   \""), "a   b", default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Inferred, tag.Source);
        Assert.Null(tag.Binding);
    }

    // --- A18: a non-finite number is nothing reported, never an exception ---

    /// <summary>
    /// A18: a JSON number the runtime parses to infinity has no canonical form — SR-1 refuses it, so
    /// a record could not swear to it. It is nothing reported for that field, and the step returns
    /// rather than throwing the canonicaliser's exception out through a shipped caller.
    /// </summary>
    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    public async Task ANonFiniteNumberIsNothingReported(string reportedNumber)
    {
        var (fabric, step) = Build();

        var result = await step.ExecuteAsync(
            new OneFieldStrategy(), Silent(reportedNumber), "estimated 6 hours", default);

        Assert.Null(fabric.GetFieldChain("Field"));
        Assert.Empty(result.MergedFields);
    }

    /// <summary>
    /// A18: and it does not take the rest of the report down with it — a second field the port did
    /// report a value for is merged exactly as it would have been.
    /// </summary>
    [Fact]
    public async Task ANonFiniteNumber_DoesNotStopTheFieldsBesideIt()
    {
        var fabric = new ContextFabric();
        var step = new TaskInferenceStep(fabric, NullLogger<TaskInferenceStep>.Instance);
        var reported = Reported("""
            {
              "AircraftId":     { "value": 1e400,    "confidence": 0.9 },
              "Title":          { "value": "Left engine oil pressure fluctuation", "confidence": 0.9 }
            }
            """);

        await step.ExecuteAsync(new WorkOrderStrategy(), reported, MeridianUtterance, default);

        Assert.Null(fabric.GetFieldChain("AircraftId"));
        Assert.Equal(ProvenanceSource.Conversation, fabric.GetFieldChain("Title")!.Current.Source);
    }

    // --- A19: span coordinates are integer-valued, and a hit keeps surrogate pairs whole ---

    /// <summary>
    /// A19: a span coordinate is an integer-valued JSON number, so a port's <c>4.0</c> is the
    /// <c>4</c> a JavaScript implementation cannot tell it from once the token is parsed. The hint
    /// here names the second occurrence, and it is honoured — which it could not be if the shape
    /// were refused.
    /// </summary>
    [Fact]
    public async Task ASpanCoordinateWrittenWithADecimalPoint_IsRead()
    {
        const string utterance = "AOG then AOG again";
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": "AOG",
                "confidence": 0.9,
                "utteranceSpan": { "start": 9.0, "end": 12.0 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, utterance, default);

        var tag = fabric.GetFieldChain("Field")!.Current;
        Assert.Equal(ProvenanceSource.Conversation, tag.Source);
        Assert.Equal(9, SpanOf(tag).Offset);
    }

    /// <summary>
    /// A19: a fractional coordinate names no span the rule can read, so the hint is discarded rather
    /// than rounded into one — and the finder runs, taking the first hit.
    /// </summary>
    [Fact]
    public async Task AFractionalSpanCoordinate_DiscardsTheHint()
    {
        const string utterance = "AOG then AOG again";
        var (fabric, step) = Build();
        var reported = Reported("""
            {
              "Field": {
                "value": "AOG",
                "confidence": 0.9,
                "utteranceSpan": { "start": 9.5, "end": 12.5 }
              }
            }
            """);

        await step.ExecuteAsync(new OneFieldStrategy(), reported, utterance, default);

        Assert.Equal(0, SpanOf(fabric.GetFieldChain("Field")!.Current).Offset);
    }

    /// <summary>
    /// A19: a hit never starts or ends inside a surrogate pair. A lone surrogate as the value text
    /// matched the leading half of a character the utterance draws, and the binding then hashed
    /// bytes the utterance does not contain — half of a code point is not text.
    /// </summary>
    /// <remarks>
    /// Asserted on the finder rather than through the step, because on .NET the port's report cannot
    /// carry the input: <c>System.Text.Json</c> refuses an unpaired surrogate when it reads the
    /// string ("Cannot read incomplete UTF-16 JSON text as string with missing low surrogate"), so
    /// this value never reaches the merge on this implementation. The guard is here anyway, and
    /// tested here, because the rule is one sentence both implementations hold to and a JavaScript
    /// string carries a lone surrogate happily. A <c>[Fact]</c> and not a <c>[Theory]</c>: xUnit
    /// serializes theory arguments and a lone surrogate does not survive the round trip.
    /// </remarks>
    [Fact]
    public void AnOccurrenceThatCutsASurrogatePair_IsNotAHit()
    {
        const string utterance = "helicopter \U0001F681 grounded";
        const string leadingHalf = "\ud83d";
        const string trailingHalf = "\ude81";

        // Both halves are in the utterance, and neither is a hit: one would end inside the pair,
        // the other would start inside it.
        Assert.Contains(leadingHalf, utterance, StringComparison.Ordinal);
        Assert.Contains(trailingHalf, utterance, StringComparison.Ordinal);
        Assert.Null(UtterancePresence.Locate(utterance, leadingHalf, null, null));
        Assert.Null(UtterancePresence.Locate(utterance, trailingHalf, null, null));
    }

    /// <summary>
    /// A19: the whole character is still found, so the rule refuses half of a pair and nothing more.
    /// </summary>
    [Fact]
    public async Task AWholeNonBmpCharacter_IsAHit()
    {
        const string utterance = "helicopter \U0001F681 grounded";
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("\"\U0001F681\""), utterance, default);

        var span = SpanOf(fabric.GetFieldChain("Field")!.Current);
        Assert.Equal("\U0001F681", utterance.Substring(span.Offset, span.Length));
        Assert.Equal(Sha256OfUtf8("\U0001F681"), span.Hash);
    }

    /// <summary>Records every log call so a test can assert on level and message without a mocking library.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
