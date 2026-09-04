namespace Affiant.Core.Tests.Primitives;

using System.Text.Json;
using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// Verifies ToolEnvelope discriminated-union serialization (invariant R2b).
/// The "$type" discriminator must route deserialization to the correct variant.
/// </summary>
public class ToolEnvelopePolymorphismTests
{
    private static readonly JsonSerializerOptions s_opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    [Fact]
    public void ReadResult_deserializes_from_json_with_type_discriminator()
    {
        var original = new ReadResult(
            ToolName: "SearchEmployees",
            Timestamp: DateTimeOffset.UtcNow,
            Summary: "Found 1 employee",
            Markdown: "| Name | Dept |\n|------|------|\n| Alice | Eng |",
            Entities: new[] { new EntityRef("Employee", "EMP-001", "Alice", new Dictionary<string, object>()) });

        // Serialize through the base type so JsonDerivedType emits the $type discriminator
        var json = JsonSerializer.Serialize<ToolEnvelope>(original, s_opts);

        Assert.Contains("\"kind\"", json);

        var result = JsonSerializer.Deserialize<ToolEnvelope>(json, s_opts);

        Assert.NotNull(result);
        Assert.IsType<ReadResult>(result);
        var readResult = (ReadResult)result;
        Assert.Equal(original.ToolName, readResult.ToolName);
        Assert.Equal(original.Markdown, readResult.Markdown);
    }

    [Fact]
    public void WriteProposal_deserializes_from_json_with_type_discriminator()
    {
        var original = new WriteProposal(
            ToolName: "RequestLeave",
            Timestamp: DateTimeOffset.UtcNow,
            Envelope: new { StartDate = "2026-05-01" });

        var json = JsonSerializer.Serialize<ToolEnvelope>(original, s_opts);

        Assert.Contains("\"kind\"", json);

        var result = JsonSerializer.Deserialize<ToolEnvelope>(json, s_opts);

        Assert.NotNull(result);
        Assert.IsType<WriteProposal>(result);
        var proposal = (WriteProposal)result;
        Assert.Equal(original.ToolName, proposal.ToolName);
    }

    [Fact]
    public void ToolError_deserializes_from_json_with_type_discriminator()
    {
        // Area-3 P2 fix round: was the bare literal "DB_CONN_TIMEOUT", which matches no framework
        // ToolErrorCodes constant — a mismatch this test's own generic serialization-roundtrip
        // purpose didn't need but which invited confusion against the real registry. Asserts the
        // actual framework constant instead (verify what the framework emits, don't invent a code).
        var original = new ToolError(
            ToolName: "SearchEmployees",
            Timestamp: DateTimeOffset.UtcNow,
            Code: ToolErrorCodes.DbTimeout,
            Message: "Database connection failed",
            Retryable: true);

        var json = JsonSerializer.Serialize<ToolEnvelope>(original, s_opts);

        Assert.Contains("\"kind\"", json);

        var result = JsonSerializer.Deserialize<ToolEnvelope>(json, s_opts);

        Assert.NotNull(result);
        Assert.IsType<ToolError>(result);
        var toolError = (ToolError)result;
        Assert.Equal(original.ToolName, toolError.ToolName);
        Assert.Equal(original.Code, toolError.Code);
        Assert.True(toolError.Retryable);
    }

    [Fact]
    public void ToolEnvelope_array_with_mixed_types_deserializes_correctly()
    {
        var envelopes = new ToolEnvelope[]
        {
            new ReadResult("Search", DateTimeOffset.UtcNow, "Summary", "# Results", Array.Empty<EntityRef>()),
            new WriteProposal("Create", DateTimeOffset.UtcNow, new { }),
            new ToolError("Search", DateTimeOffset.UtcNow, ToolErrorCodes.DbTimeout, "Timed out", true),
        };

        var json = JsonSerializer.Serialize(envelopes, s_opts);
        var result = JsonSerializer.Deserialize<ToolEnvelope[]>(json, s_opts);

        Assert.NotNull(result);
        Assert.Equal(3, result.Length);
        Assert.IsType<ReadResult>(result[0]);
        Assert.IsType<WriteProposal>(result[1]);
        Assert.IsType<ToolError>(result[2]);
    }
}
