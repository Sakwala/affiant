namespace Affiant.Core.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Affiant.Abstractions.Models;
using Affiant.Abstractions.Serialization;
using Affiant.Core.Serialization;

/// <summary>
/// The Docket entry id a proposal gets, derived and never invented (GT-4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why derived.</b> The same proposal, made twice in the same conversation, is one row: a model
/// that retries a tool call must replay the entry it already filed rather than file a second one a
/// reviewer would have to decide twice. Two tenants cannot collide by accident either, which is
/// what makes the gate's scoped replay lookup safe to treat a miss as a fresh filing.
/// </para>
/// <para>
/// <b>Why exactly this material, in exactly this form.</b> The id is not private to an
/// implementation: it appears inside the record, in the <c>reviewer-act</c> binding an accepted
/// amendment mints (PV-2), so it is inside the canonical form and inside the content hash an
/// execution grant binds to (SR-1). Two implementations that derived different ids for the same
/// proposal would disagree about which row a proposal <i>is</i>, and no grant minted by one would
/// validate against the other. The material is the tenant, the conversation, the tool name, the
/// operation and the arguments — with <c>supersedes</c> present only when the proposal replaces a
/// row, so a first filing's id is what it would have been before resubmission existed — written as
/// SR-1's canonical JSON, digested with SHA-256, and laid out as a version-8 UUID.
/// </para>
/// </remarks>
public static class EntryIdDerivation
{
    /// <summary>The id for one proposal (GT-4).</summary>
    /// <param name="tenantId">The tenant the conversation belongs to.</param>
    /// <param name="conversationId">The conversation the call was made in.</param>
    /// <param name="toolName">The tool the model called.</param>
    /// <param name="operation">
    /// The write as the host declared it — its shape, the entity it names and the fields it
    /// proposes, in the declared order. Stated rather than read off the record: a projection that
    /// reordered fields would otherwise change which row a proposal is. Where no declaration exists
    /// — a resubmission has only the stored record to read — <see cref="ProposedOperation.From"/> is
    /// the reading, which is what the protocol's reference implementation does on that path too.
    /// </param>
    /// <param name="arguments">The arguments the model passed, or <see langword="null"/> for none.</param>
    /// <param name="supersedes">The row this proposal replaces, or <see langword="null"/>.</param>
    public static Guid Derive(
        string tenantId,
        string conversationId,
        string toolName,
        ProposedOperation operation,
        IReadOnlyDictionary<string, object?>? arguments,
        Guid? supersedes)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var fields = new JsonArray();
        foreach (var field in operation.Fields) fields.Add(JsonValue.Create(field));

        var material = new JsonObject
        {
            ["tenantId"] = tenantId,
            ["conversationId"] = conversationId,
            ["toolName"] = toolName,
            ["operation"] = new JsonObject
            {
                ["kind"] = operation.Kind,
                ["entityType"] = operation.EntityType,
                ["entityId"] = operation.EntityId,
                ["fields"] = fields,
            },
            ["args"] = arguments is null
                ? null
                : JsonSerializer.SerializeToNode(arguments, AffiantJson.SerializerOptions),
        };

        // Present only when there IS one: an absent property and a null one are different documents
        // under SR-1, and a first filing's id must be what it was before resubmission existed.
        if (supersedes is { } parent) material["supersedes"] = parent.ToString();

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalSerializer.CanonicalString(material))));

        return UuidFrom(digest);
    }

    /// <summary>
    /// The first 128 bits of <paramref name="digest"/> as a UUID with version 8 and the RFC 9562
    /// variant — a name-based id whose name is the proposal.
    /// </summary>
    private static Guid UuidFrom(string digest)
    {
        var nibbles = digest[..32].ToCharArray();
        nibbles[12] = '8';
        nibbles[16] = "89ab"[Convert.ToInt32(nibbles[16].ToString(), 16) % 4];

        var hex = new string(nibbles);
        return Guid.Parse(
            $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}");
    }
}
