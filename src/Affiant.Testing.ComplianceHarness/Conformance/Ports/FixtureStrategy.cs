using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Testing.ComplianceHarness.Conformance.Model;

namespace Affiant.Testing.ComplianceHarness.Conformance.Ports;

/// <summary>
/// The field schema a step declares, expressed as the framework's own
/// <see cref="ITaskInferenceStrategy"/> — the shape <c>SchemaDrivenAffidavitProjection</c> is driven
/// by, and the only place the framework learns a field's kind, whether it is mandatory, and its
/// presentation constraints.
/// </summary>
/// <remarks>
/// The declared order is the Affidavit's field order, which is what a fixture stating
/// <c>affidavit.fields</c> asserts exactly (AF-1).
/// </remarks>
internal sealed class FixtureStrategy(string entityName, IReadOnlyList<TaskInferenceField> fields) : ITaskInferenceStrategy
{
    public string EntityName => entityName;

    public IReadOnlyList<TaskInferenceField> Fields => fields;

    /// <summary>
    /// No floor. A fixture's inference port reports exactly what it was scripted to report, and a
    /// threshold here would filter it — which is precisely what <c>RUNNER.md</c> §7 forbids the
    /// port to do.
    /// </summary>
    public double? MinimumConfidenceThreshold => null;

    /// <summary>Builds the schema a <c>file</c> step declares, from its prepared fields, its schema clause and its operation.</summary>
    public static FixtureStrategy ForFile(StepSpec step)
    {
        var names = new List<string>();
        foreach (var name in (step.Operation?.Fields ?? []).Concat(
                     (step.PreparedFields ?? []).Select(f => f.Name)).Concat(
                     (step.Schema ?? []).Select(f => f.Name)))
        {
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        var declared = new List<TaskInferenceField>();
        foreach (var name in names)
        {
            var prepared = (step.PreparedFields ?? []).FirstOrDefault(f => f.Name == name);
            var schema = (step.Schema ?? []).FirstOrDefault(f => f.Name == name);
            declared.Add(Declare(
                name,
                schema?.Kind ?? prepared?.Kind ?? "text",
                schema?.Description,
                schema?.Required ?? prepared?.IsMandatory ?? false,
                schema?.AllowedValues,
                schema?.Pattern));
        }

        return new FixtureStrategy(step.Operation!.EntityType, declared);
    }

    /// <summary>Builds the schema a wrapped tool declares (<c>wrap-execute</c>).</summary>
    public static FixtureStrategy ForTool(ToolSpec tool) => new(
        tool.EntityType,
        tool.Fields.Select(f => Declare(f.Name, f.Kind, f.Description, f.Required, f.AllowedValues, f.Pattern)).ToArray());

    private static TaskInferenceField Declare(
        string name, string kind, string? description, bool required, IReadOnlyList<string>? allowedValues, string? pattern) =>
        new(
            Name: name,
            JsonType: kind switch { "number" => "number", _ => "string" },
            Description: description ?? name,
            Pattern: pattern,
            Enum: allowedValues,
            Required: required,
            Format: kind switch { "date" => "date", _ => null });
}
