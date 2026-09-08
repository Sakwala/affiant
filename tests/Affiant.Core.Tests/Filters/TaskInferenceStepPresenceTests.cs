namespace Affiant.Core.Tests.Filters;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Filters;
using Affiant.Core.Services;
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
    /// The value text is the raw JSON token, so a number the model wrote as 6.0 is not the 6 the
    /// person typed. Honest: the record swears to 6.0, and 6.0 is not in the turn.
    /// </summary>
    [Fact]
    public async Task ANumberWhoseJsonTokenDiffersFromTheTypedForm_DoesNotHit()
    {
        var (fabric, step) = Build();

        await step.ExecuteAsync(new OneFieldStrategy(), Silent("6.0"), "estimated 6 hours", default);

        Assert.Equal(ProvenanceSource.Inferred, fabric.GetFieldChain("Field")!.Current.Source);
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
}
