using System.Reflection;
using Affiant.Abstractions.Attributes;
using Affiant.Abstractions.Models;
using Xunit;

namespace Affiant.Abstractions.Tests.Spec;

// Enforces parity between the §3.11 spec table and the live source records.
// Spec drift becomes a CI failure rather than a manual review burden.
public sealed class DescriptorSpecSyncTests
{
    // Walks up from the test assembly directory until the repo-root Affiant.slnx is found,
    // then returns the framework spec adjacent to it under docs/.
    // Robust to: dotnet test from any directory, IDE test runners, CI with arbitrary working-directory.
    private static string ResolveSpecPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Affiant.slnx")))
                return Path.Combine(dir.FullName, "docs", "affiant-framework-specification.md");
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not find the Affiant.slnx anchor walking up from AppContext.BaseDirectory. " +
            "Test cannot locate the framework spec. Check your working directory or CI configuration.");
    }

    // Splits the markdown at header boundaries and returns the content of the named section.
    // Stops at the next header of the same or higher level (fewer or equal # characters).
    private static string ExtractSection(string markdown, string sectionHeader)
    {
        var lines = markdown.Split('\n');
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r', ' ') == sectionHeader)
            {
                start = i + 1;
                break;
            }
        }
        if (start < 0)
            throw new InvalidOperationException(
                $"Could not find section header '{sectionHeader}' in the framework spec. " +
                "Verify that Story 15.7 updates to affiant-framework-specification.md are present.");

        int level = sectionHeader.TakeWhile(c => c == '#').Count();
        var sb = new System.Text.StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('#'))
            {
                int thisLevel = lines[i].TrimStart().TakeWhile(c => c == '#').Count();
                if (thisLevel <= level)
                    break;
            }
            sb.AppendLine(lines[i]);
        }
        return sb.ToString();
    }

    // Parses a GitHub-flavored markdown table expecting columns: Field, Required, Purpose.
    // Tolerant of trailing whitespace and extra columns; only the first three named columns matter.
    // Throws with a diagnostic excerpt if no recognizable table header is found.
    private static IReadOnlyList<(string Field, string Required, string Purpose)> ParseFieldSetTable(string sectionContent)
    {
        var results = new List<(string, string, string)>();
        var lines = sectionContent.Split('\n');
        int tableStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r', ' ');
            if (trimmed.StartsWith("| Field") || trimmed.StartsWith("| `Field`"))
            {
                tableStart = i;
                break;
            }
        }
        if (tableStart < 0)
            throw new InvalidOperationException(
                "Could not find the field-set table (expected a row starting with '| Field') in §3.11.2. " +
                $"Section content excerpt: {sectionContent[..Math.Min(300, sectionContent.Length)]}");

        // Row 0 = header, row 1 = separator (---|---), row 2+ = data.
        for (int i = tableStart + 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('|'))
                break;
            // Skip pure separator rows.
            if (line.All(c => c == '|' || c == '-' || c == ' '))
                break;

            // Split on | — [0] is empty, [1] = Field, [2] = Required, [3] = Purpose.
            var cells = line.Split('|', StringSplitOptions.None);
            if (cells.Length < 4)
                break;

            var field = cells[1].Trim().Trim('`');
            var required = cells[2].Trim();
            var purpose = cells.Length > 3 ? cells[3].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(field))
                break;

            results.Add((field, required, purpose));
        }
        return results;
    }

    // Uses NullabilityInfoContext (available since .NET 6) to detect nullable reference types.
    // Works correctly for primary-constructor record properties in .NET 10.
    private static bool IsNullable(PropertyInfo property)
    {
        var context = new NullabilityInfoContext();
        var info = context.Create(property);
        return info.WriteState == NullabilityState.Nullable
            || info.ReadState == NullabilityState.Nullable;
    }

    [Fact]
    public void DescriptorFieldSetTable_MatchesAffiantToolDescriptorRecord()
    {
        var spec = File.ReadAllText(ResolveSpecPath());
        var subsection = ExtractSection(spec, "#### 3.11.2 The `AffiantToolDescriptor` Field Set");
        var table = ParseFieldSetTable(subsection);

        var properties = typeof(AffiantToolDescriptor)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Name: p.Name, IsNullable: IsNullable(p)))
            .ToList();

        // Every source property must have a spec table row with matching nullability.
        foreach (var prop in properties)
        {
            var row = table.FirstOrDefault(r => r.Field == prop.Name);
            Assert.True(row.Field is not null,
                $"Spec §3.11.2 table has no row for AffiantToolDescriptor.{prop.Name}. " +
                "Add the missing row to docs/affiant-framework-specification.md §3.11.2.");

            var expectedRequired = prop.IsNullable ? "no" : "yes";
            Assert.True(
                expectedRequired == row.Required,
                $"AffiantToolDescriptor.{prop.Name} nullability mismatch: " +
                $"property is {(prop.IsNullable ? "nullable" : "non-nullable")} " +
                $"but spec table says Required='{row.Required}'. " +
                "Fix the 'Required' cell in §3.11.2 to match the property's C# nullability.");
        }

        // Every spec table row must correspond to a source property.
        foreach (var row in table)
        {
            Assert.True(properties.Any(p => p.Name == row.Field),
                $"Spec §3.11.2 table row '{row.Field}' does not correspond to any AffiantToolDescriptor property. " +
                "Remove the stale row from §3.11.2 or rename the source property (audit G0 Item 2 first).");
        }
    }

    [Fact]
    public void AttributeTable_MatchesAffiantWriteToolAttribute()
    {
        var spec = File.ReadAllText(ResolveSpecPath());
        // Reflect on constructor parameters (lowercase) per G0 Item 4 — the constructor names are the
        // ratified API surface. Property names are capitalized and would not match the spec table literally.
        var ctor = typeof(AffiantWriteToolAttribute).GetConstructors().Single();
        var parameters = ctor.GetParameters().Select(p => p.Name!).ToList();

        var subsection = ExtractSection(spec, "#### 3.11.4 The `[AffiantWriteTool]` Attribute");

        // Each constructor parameter name must appear in the subsection (case-insensitive, in the table).
        foreach (var paramName in parameters)
        {
            Assert.True(
                subsection.Contains(paramName, StringComparison.OrdinalIgnoreCase),
                $"Spec §3.11.4 attribute table does not mention constructor parameter '{paramName}'. " +
                "Add it to the parameter table in docs/affiant-framework-specification.md §3.11.4.");
        }
    }

    [Fact]
    public void SpecFile_ContainsRequiredSubsections()
    {
        var spec = File.ReadAllText(ResolveSpecPath());
        var expectedSubsections = new[]
        {
            "#### 3.11.1",
            "#### 3.11.2",
            "#### 3.11.3",
            "#### 3.11.4",
            "#### 3.11.5",
            "#### 3.11.6",
        };
        foreach (var sub in expectedSubsections)
        {
            Assert.True(
                spec.Contains(sub, StringComparison.Ordinal),
                $"Framework spec is missing subsection header '{sub}'. " +
                "Verify docs/affiant-framework-specification.md §3.11 is intact.");
        }
    }

    [Fact]
    public void SpecFile_ContainsCheckATemplateSubstring()
    {
        var spec = File.ReadAllText(ResolveSpecPath());
        // G0 Item 5 ratified Check A error message — lock the identifying substring.
        const string checkASubstring = "[KernelFunction]` methods are not registered as Affiant tool descriptors";
        Assert.True(
            spec.Contains(checkASubstring, StringComparison.Ordinal),
            $"Framework spec §3.11.5 is missing the G0-ratified Check A error-message template substring: '{checkASubstring}'.");
    }

    [Fact]
    public void SpecFile_ContainsCheckBTemplateSubstring()
    {
        var spec = File.ReadAllText(ResolveSpecPath());
        // G0 Item 5 ratified Check B error message — lock the identifying substring.
        const string checkBSubstring = "inference strategy that cannot be resolved from `IServiceProvider`";
        Assert.True(
            spec.Contains(checkBSubstring, StringComparison.Ordinal),
            $"Framework spec §3.11.5 is missing the G0-ratified Check B error-message template substring: '{checkBSubstring}'.");
    }

    [Fact]
    public void AttributeAllowMultiple_IsFalse()
    {
        // G0 Item 4 ratified AllowMultiple = false — lock it with a reflection assertion.
        var usage = typeof(AffiantWriteToolAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.False(usage!.AllowMultiple,
            "AffiantWriteToolAttribute must have AllowMultiple = false per G0 Item 4 (2026-05-14). " +
            "Changing this is a breaking API change.");
    }
}
