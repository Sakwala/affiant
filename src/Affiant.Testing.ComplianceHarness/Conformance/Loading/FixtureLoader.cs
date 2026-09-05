using System.Globalization;
using System.Text.Json.Nodes;
using Affiant.Testing.ComplianceHarness.Conformance.Model;
using Json.Schema;

namespace Affiant.Testing.ComplianceHarness.Conformance.Loading;

/// <summary>Raised when a fixture document does not survive the checks that make it a test.</summary>
/// <remarks>
/// The runner checks the document before it runs it and a fixture that fails that check is not run
/// at all (<c>RUNNER.md</c> §6): running it would report a pass, and a pass is the one answer it
/// must never give.
/// </remarks>
internal sealed class FixtureDocumentException(string message) : Exception(message);

/// <summary>Turns a vendored JSON document into the typed model the executor binds to the framework.</summary>
internal static class FixtureLoader
{
    private static readonly JsonSchema Schema = JsonSchema.FromFile(
        Path.Combine(ProtocolSuite.Instance.Root, "fixture.schema.json"));

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = false,
    };

    /// <summary>
    /// Load and check one fixture. Throws <see cref="FixtureDocumentException"/> for a document the
    /// format refuses — an unknown key anywhere, an expectation that states no fact, a telemetry
    /// clause naming a key the registry does not know.
    /// </summary>
    public static Fixture Load(string path)
    {
        var doc = ProtocolSuite.ReadObject(path);
        Validate(doc, path);

        var expect = doc["expect"]!.AsObject();
        RefuseVacuous(expect, path);
        RefuseUnregisteredTelemetry(expect, path);

        var given = doc["given"]!.AsObject();
        return new Fixture(
            doc["id"]!.GetValue<string>(),
            doc["rules"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray(),
            doc["title"]!.GetValue<string>(),
            ReadGiven(given),
            expect,
            path);
    }

    /// <summary>
    /// Load one canonical byte vector (<c>RUNNER.md</c> §9) — a different document shape.
    /// </summary>
    /// <remarks>
    /// The document is held against <c>canonical-vector.schema.json</c> by
    /// <see cref="Canonical.CanonicalVectorRunner"/> before it is run, so a malformed vector is an
    /// error in the run rather than a pass.
    /// </remarks>
    public static CanonicalVector LoadVector(string path)
    {
        var doc = ProtocolSuite.ReadObject(path);
        return new CanonicalVector(
            doc["id"]!.GetValue<string>(),
            doc["rules"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray(),
            doc["note"]!.GetValue<string>(),
            doc["input"]!.AsObject(),
            doc["amendments"] as JsonObject,
            doc["reviewerAct"] as JsonObject,
            doc["amendedInput"] as JsonObject,
            doc["expectedBytesUtf8"]!.GetValue<string>(),
            doc["expectedSha256"]!.GetValue<string>(),
            path);
    }

    private static void Validate(JsonObject doc, string path)
    {
        var result = Schema.Evaluate(doc, Options);
        if (result.IsValid)
        {
            return;
        }

        // An unknown key anywhere fails the fixture, NAMING ITS PATH: `statuz` for `status` is a key
        // the checker never reads, so the fixture asserts nothing about that fact and every
        // implementation passes it, including one that does nothing.
        var problems = Flatten(result)
            .Where(d => !d.IsValid && d.Errors is { Count: > 0 })
            .Select(d => $"  {(string.IsNullOrEmpty(d.InstanceLocation.ToString()) ? "(root)" : d.InstanceLocation.ToString())}: {string.Join("; ", d.Errors!.Values)}")
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();

        throw new FixtureDocumentException(
            $"{Path.GetFileName(path)} does not validate against fixture.schema.json:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;
        foreach (var child in results.Details.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    /// <summary>
    /// An <c>expect</c> that states no fact fails as vacuous. The count is of leaf facts stated, not
    /// of comparisons performed — a run also performs the card invariants, and a vacuous fixture
    /// with a filing step would otherwise clear that bar without stating a thing.
    /// </summary>
    private static void RefuseVacuous(JsonObject expect, string path)
    {
        if (CountLeaves(expect) == 0)
        {
            throw new FixtureDocumentException($"{Path.GetFileName(path)} states no fact: expect is vacuous.");
        }
    }

    private static int CountLeaves(JsonNode? node) => node switch
    {
        JsonObject o => o.Sum(kv => CountLeaves(kv.Value)),
        JsonArray a => a.Count == 0 ? 0 : a.Sum(CountLeaves),
        _ => 1,
    };

    private static void RefuseUnregisteredTelemetry(JsonObject expect, string path)
    {
        foreach (var clause in new[] { "telemetry", "telemetryAbsent" })
        {
            if (expect[clause] is not JsonArray keys)
            {
                continue;
            }

            foreach (var key in keys.Select(k => k!.GetValue<string>()))
            {
                if (!ProtocolSuite.Instance.TelemetryRegistry.Contains(key))
                {
                    throw new FixtureDocumentException(
                        $"{Path.GetFileName(path)} names telemetry key \"{key}\", which the registry does not know (TL-1).");
                }
            }
        }
    }

    private static GivenSpec ReadGiven(JsonObject given) => new(
        Instant(given["clock"]!),
        given["store"]?.GetValue<string>() ?? "memory",
        ReadGate(given["gate"]!.AsObject()),
        ReadCtx(given["ctx"]!.AsObject()),
        (given["prior"] as JsonArray ?? []).Select(s => ReadStep(s!.AsObject())).ToArray(),
        ReadStep(given["step"]!.AsObject()));

    private static GateSpec ReadGate(JsonObject gate)
    {
        var auth = gate["authorization"]!.AsObject();
        var entities = new Dictionary<string, IReadOnlyDictionary<string, JsonNode?>>(StringComparer.Ordinal);
        if (gate["entities"] is JsonObject table)
        {
            foreach (var (key, value) in table)
            {
                entities[key] = value!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal);
            }
        }

        var inference = new Dictionary<string, InferredFieldSpec>(StringComparer.Ordinal);
        if (gate["inference"] is JsonObject scripted)
        {
            foreach (var (name, value) in scripted)
            {
                var f = value!.AsObject();
                inference[name] = new InferredFieldSpec(
                    f["value"]?.DeepClone(),
                    f["confidence"]!.GetValue<double>(),
                    f["presence"]!.GetValue<string>(),
                    f["utteranceSpan"] as JsonObject);
            }
        }

        return new GateSpec(
            gate["defaultTtlMs"]!.GetValue<int>(),
            new AuthorizationSpec(
                auth["allow"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray(),
                auth["throws"]?.GetValue<bool>() ?? false),
            (gate["policies"] as JsonArray ?? []).Select(p => ReadPolicy(p!.AsObject())).ToArray(),
            gate["riskScorer"] is { } scorer && scorer.GetValueKind() is System.Text.Json.JsonValueKind.Number ? scorer.GetValue<double>() : null,
            gate.ContainsKey("riskScorer"),
            (gate["interceptors"] as JsonArray ?? []).Select(i => ReadInterceptor(i!.AsObject())).ToArray(),
            inference,
            entities,
            gate.ContainsKey("entities"),
            (gate["uncovered"] as JsonArray ?? []).Select(u => new UncoveredSpec(
                u!["tool"]!.GetValue<string>(), u["category"]!.GetValue<string>())).ToArray(),
            gate["sessions"]?.GetValue<bool>() ?? true);
    }

    private static PolicySpec ReadPolicy(JsonObject p) => new(
        p["id"]!.GetValue<string>(),
        p["version"]!.GetValue<string>(),
        (p["declaredInputs"] as JsonArray ?? []).Select(d => d!.GetValue<string>()).ToArray(),
        p["declaresThreshold"]?.GetValue<bool>() ?? false,
        p["defaultTtlMs"] is { } t && t.GetValueKind() is System.Text.Json.JsonValueKind.Number ? t.GetValue<int>() : null,
        p["verdict"] is JsonObject v
            ? new VerdictSpec(
                v["requirement"]!.GetValue<string>(),
                v["ttlMs"]?.GetValue<int>(),
                v["threshold"]?.GetValue<double>(),
                v["reason"]?.GetValue<string>())
            : null);

    private static InterceptorSpec ReadInterceptor(JsonObject i)
    {
        var fields = new Dictionary<string, InterceptedFieldSpec>(StringComparer.Ordinal);
        foreach (var (name, value) in i["fields"]!.AsObject())
        {
            var f = value!.AsObject();
            fields[name] = new InterceptedFieldSpec(
                f["value"]?.DeepClone(),
                f["source"]!.GetValue<string>(),
                f["confidence"]!.GetValue<double>(),
                ReadBinding(f["binding"]),
                f["evidence"]?.GetValue<string>());
        }

        return new InterceptorSpec(i["name"]!.GetValue<string>(), fields);
    }

    private static BindingSpec? ReadBinding(JsonNode? node) => node is JsonObject b
        ? new BindingSpec(b["kind"]!.GetValue<string>(), b["ref"]!.AsObject())
        : null;

    private static CtxSpec ReadCtx(JsonObject ctx) => new(
        ctx["tenantId"]!.GetValue<string>(),
        ctx["conversationId"]!.GetValue<string>(),
        ctx["channel"]!.GetValue<string>(),
        ReadPrincipal(ctx["principal"]),
        ctx["utterance"]?.GetValue<string>(),
        ctx["messageId"]?.GetValue<string>());

    private static PrincipalSpec? ReadPrincipal(JsonNode? node)
    {
        if (node is not JsonObject p)
        {
            return null;
        }

        var relay = p["relay"] is JsonObject r
            ? new RelaySpec(r["channelIdentity"]!.GetValue<string>(), r["messageId"]!.GetValue<string>())
            : null;
        return new PrincipalSpec(p["kind"]!.GetValue<string>(), p["id"]!.GetValue<string>(), relay, p["assertedMember"]?.GetValue<string>());
    }

    private static StepSpec ReadStep(JsonObject s)
    {
        var step = new StepSpec(
            s["kind"]!.GetValue<string>(),
            s["as"]?.GetValue<string>(),
            s["at"] is { } at ? Instant(at) : null,
            ReadPrincipal(s["principal"]),
            s.ContainsKey("principal"),
            s["tenantId"]?.GetValue<string>(),
            s["conversationId"]?.GetValue<string>(),
            s["entry"]?.GetValue<string>(),
            s["refusal"]?.GetValue<string>(),
            s.ContainsKey("refusal"),
            s);

        return step.Kind switch
        {
            "wrap-execute" => step with
            {
                Tool = ReadTool(s["tool"]!.AsObject()),
                Args = s["args"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal),
            },
            "file" => step with
            {
                ToolName = s["toolName"]!.GetValue<string>(),
                Operation = ReadOperation(s["operation"]!.AsObject()),
                PreparedFields = (s["preparedFields"] as JsonArray)?.Select(f => ReadPreparedField(f!.AsObject())).ToArray(),
                Schema = (s["schema"] as JsonArray)?.Select(f => ReadToolField(f!.AsObject())).ToArray(),
                Args = (s["args"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal),
                OperationLabel = s["operationLabel"]?.GetValue<string>(),
            },
            "decide" => step with { Decision = ReadDecision(s["decision"]!.AsObject()) },
            "markExecuted" => step with
            {
                Outcome = s["outcome"]!.GetValue<string>(),
                Detail = s["detail"]?.GetValue<string>(),
            },
            "expireDue" => step with { Limit = s["limit"]!.GetValue<int>(), Scope = ReadScope(s["scope"]) },
            "rehydrate" => step with
            {
                Page = new PageSpec(s["page"]!["limit"]!.GetValue<int>(), s["page"]!["cursor"]?.GetValue<string>()),
                Scope = ReadScope(s["scope"]),
            },
            _ => step,
        };
    }

    private static ScopeSpec? ReadScope(JsonNode? node) => node is JsonObject s
        ? new ScopeSpec(s["tenantId"]?.GetValue<string>(), s["conversationId"]?.GetValue<string>())
        : null;

    private static ToolSpec ReadTool(JsonObject t) => new(
        t["name"]!.GetValue<string>(),
        t["description"]?.GetValue<string>(),
        t["entityType"]!.GetValue<string>(),
        t["entityId"]?.GetValue<string>(),
        t["writeCapable"]?.GetValue<bool>() ?? true,
        t["executedBy"]?.GetValue<string>(),
        t["hostedMcp"]?.GetValue<bool>() ?? false,
        t["omitExecute"]?.GetValue<bool>() ?? false,
        t["operationLabel"]?.GetValue<string>(),
        t["fields"]!.AsArray().Select(f => ReadToolField(f!.AsObject())).ToArray());

    private static ToolFieldSpec ReadToolField(JsonObject f) => new(
        f["name"]!.GetValue<string>(),
        f["kind"]!.GetValue<string>(),
        f["description"]?.GetValue<string>(),
        f["required"]?.GetValue<bool>() ?? false,
        (f["allowedValues"] as JsonArray)?.Select(v => v!.GetValue<string>()).ToArray(),
        f["pattern"]?.GetValue<string>());

    private static OperationSpec ReadOperation(JsonObject o) => new(
        o["kind"]!.GetValue<string>(),
        o["entityType"]!.GetValue<string>(),
        o["entityId"]?.GetValue<string>(),
        o["fields"]!.AsArray().Select(f => f!.GetValue<string>()).ToArray());

    private static PreparedFieldSpec ReadPreparedField(JsonObject f) => new(
        f["name"]!.GetValue<string>(),
        f["kind"]!.GetValue<string>(),
        f["value"]?.DeepClone(),
        f["isMandatory"]?.GetValue<bool>() ?? false,
        f["provenance"] is JsonObject p
            ? new ProvenanceSpec(
                p["source"]!.GetValue<string>(),
                p["confidence"]!.GetValue<double>(),
                ReadBinding(p["binding"]),
                p["note"]?.GetValue<string>())
            : null,
        f.ContainsKey("provenance"));

    private static DecisionSpec ReadDecision(JsonObject d) => new(
        d["kind"]!.GetValue<string>(),
        (d["amendments"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone(), StringComparer.Ordinal),
        d.ContainsKey("amendments"),
        d["reason"]?.GetValue<string>());

    private static DateTimeOffset Instant(JsonNode node) => DateTimeOffset.Parse(
        node.GetValue<string>(),
        CultureInfo.InvariantCulture,
        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
