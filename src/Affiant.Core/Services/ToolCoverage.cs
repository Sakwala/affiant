namespace Affiant.Core.Services;

using System.Collections.Concurrent;
using Affiant.Abstractions.Exceptions;
using Affiant.Abstractions.Models;
using Affiant.Core.Observability;

/// <summary>
/// What the gate can and cannot stand in front of (CV-4).
///
/// <para>
/// Affiant intercepts a write by <b>being</b> the tool that performs it. Some write-capable tools
/// cannot be intercepted at all: one the model provider executes on its own side, one a hosted MCP
/// server performs, one declared write-capable with no execute step for the gate to replace. A write
/// made through any of those reaches a system of record with no Affidavit, no reviewer and no row —
/// and, until this type existed, looked from the outside exactly like a write that had been through
/// the gate.
/// </para>
///
/// <para>
/// Two halves, because there are two moments at which the gap is knowable. A tool list a host wires
/// up is audited at <b>wire-up</b> and refused there, loudly, before anything can be proposed. A
/// tool a host knows it cannot cover and declares anyway — a relay capture arriving from a channel
/// the framework does not sit in front of — is filed, and the row it produces carries the marker
/// saying so, so no decision on it is ever accepted and a reviewer surface can say why.
/// </para>
/// </summary>
public sealed class ToolCoverage
{
    private readonly ConcurrentDictionary<string, CoverageCategory> _declared = new(StringComparer.Ordinal);

    /// <summary>
    /// Declare that <paramref name="toolName"/> is write-capable and cannot be covered by the gate.
    /// </summary>
    /// <remarks>
    /// A declaration is an admission, not a waiver: every entry filed for the tool is blocked with
    /// the category, no decision on such an entry is accepted, and the card says so in words. A host
    /// that wants the write to go through has to make the tool coverable.
    /// </remarks>
    /// <param name="toolName">The tool as the host names it.</param>
    /// <param name="category">Why it cannot be covered.</param>
    public void DeclareUncovered(string toolName, CoverageCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        _declared[toolName] = category;
    }

    /// <summary>
    /// The marker an entry filed for <paramref name="toolName"/> carries, or <see langword="null"/>
    /// when the tool is covered.
    /// </summary>
    public BlockedMarker.CoverageRefused? MarkerFor(string toolName) =>
        toolName is not null && _declared.TryGetValue(toolName, out var category)
            ? new BlockedMarker.CoverageRefused(category, toolName)
            : null;

    /// <summary>Whether any tool has been declared uncovered.</summary>
    public bool Any => !_declared.IsEmpty;

    /// <summary>
    /// Refuse a write-capable tool the gate cannot stand in front of, at wire-up (CV-1, CV-4).
    /// </summary>
    /// <remarks>
    /// Raised where the host wires the tool up rather than where the write is proposed: a coverage
    /// gap must not be discoverable only by the write it silently let through. The refusal carries
    /// the protocol's <c>coverage-refused</c> code and names the category, and one
    /// <c>coverage.refused</c> event is emitted per tool before the throw, so a collector can count
    /// which tools an adopter keeps trying to wire up uncovered.
    /// </remarks>
    /// <param name="toolName">The tool as the host names it.</param>
    /// <param name="category">Why the gate cannot stand in front of it.</param>
    /// <exception cref="AffiantCoverageException">Always.</exception>
    public static void Refuse(string toolName, CoverageCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var spelled = Spell(category);
        AffiantTelemetry.RecordCoverageRefused(toolName, spelled, "wire-up");

        throw new AffiantCoverageException(
            $"CV-4: the write-capable tool '{toolName}' is {spelled} — the gate cannot stand in " +
            "front of it, so a write made through it would reach a system of record with no " +
            "Affidavit, no reviewer and no Docket row. Make the tool coverable, or declare it " +
            "uncovered so every entry it files is blocked and says why.");
    }

    /// <summary>
    /// Audit one tool a host is wiring up, refusing it when the gate cannot cover it (CV-4).
    /// </summary>
    /// <param name="toolName">The tool as the host names it.</param>
    /// <param name="writeCapable">Whether the tool performs a write.</param>
    /// <param name="category">
    /// Why the gate cannot cover it, or <see langword="null"/> when it can. A covered tool and a
    /// read-only tool are both accepted in silence.
    /// </param>
    public static void Audit(string toolName, bool writeCapable, CoverageCategory? category)
    {
        if (!writeCapable || category is not { } uncoverable) return;
        Refuse(toolName, uncoverable);
    }

    /// <summary>The category as the wire spells it.</summary>
    public static string Spell(CoverageCategory category) => category switch
    {
        CoverageCategory.NoExecute => "no-execute",
        CoverageCategory.ProviderExecuted => "provider-executed",
        _ => "hosted-mcp",
    };
}
