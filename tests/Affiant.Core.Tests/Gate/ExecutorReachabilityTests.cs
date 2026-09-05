namespace Affiant.Core.Tests.Gate;

using System.Reflection;
using Affiant.Abstractions.Interfaces;
using Affiant.Core.Services;
using Xunit;

/// <summary>
/// AZ-7: the framework never performs the write. No package writes to a host's store; the executor
/// is host code the host runs against an attested Docket entry; the framework exposes no
/// <c>execute</c> and ships no default executor, and the only path to an executed write is the
/// host's own report.
/// </summary>
/// <remarks>
/// <para>
/// Two independent guards, because they fail in different ways. The <b>source scan</b> catches a
/// framework class that resolves an <see cref="IWriteExecutor"/> and calls it — the shape that
/// would quietly turn the gate into a writer. The <b>surface check</b> catches a framework type
/// that takes, returns or implements one, which is how such a call site would normally be wired,
/// and it holds against the compiled assembly rather than the text.
/// </para>
/// <para>
/// The rule is not "the framework should not write"; it is that there is nowhere in the framework
/// for a write to happen, so a reviewer does not have to take anybody's word for it.
/// </para>
/// </remarks>
public class ExecutorReachabilityTests
{
    private const string Port = nameof(IWriteExecutor);

    /// <summary>The one file allowed to name the port in code: its own declaration.</summary>
    private const string DeclarationFile = "IWriteExecutor.cs";

    /// <summary>
    /// The one place under <c>src/</c> allowed to register an executor, and what it must be.
    /// </summary>
    /// <remarks>
    /// The conformance runner in <c>Affiant.Testing.ComplianceHarness</c> arms a TRIPWIRE executor
    /// for every fixture: GT-6 says the gate stands in front of a write and never performs one, and
    /// the way to measure that is to put an executor where the gate could reach it and assert it was
    /// never called. That is the opposite of a path to a write, and the second test below holds it
    /// to that — the only implementation in the harness must throw when invoked. Nothing else under
    /// <c>src/</c> may name the port at all, and
    /// <c>Affiant.Conformance.Tests.ConformanceDriverTests.TheOnlyExecutorInAShippedAssembly_IsATripwireThatThrows</c>
    /// — which can see that assembly, where this project cannot — holds it to throwing.
    /// </remarks>
    private const string HarnessRunner =
        "Affiant.Testing.ComplianceHarness" + "/" + "Conformance" + "/";

    [Fact]
    public void NoFrameworkSourceFile_CallsAnExecutor()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == DeclarationFile) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                var code = line.TrimStart();

                // Documentation may name the port — a host reading IWriteExecutor's contract from
                // ReviewOutcome's remarks is exactly what those remarks are for. Code may not.
                if (code.StartsWith("///", StringComparison.Ordinal)) continue;
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!code.Contains(Port, StringComparison.Ordinal)) continue;

                var relative = Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith(HarnessRunner, StringComparison.Ordinal)) continue;

                offenders.Add($"{relative}:{lineNumber}: {code}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "AZ-7: no code under src/ may name IWriteExecutor — the executor is host code the host " +
            "runs, and the framework has no path to it. Found:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NoFrameworkType_Implements_Takes_OrReturnsAnExecutor()
    {
        // Every Affiant assembly this test project can see, which is the whole DAG below the
        // adapters plus the two the gate itself is exercised against.
        var assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Affiant.", StringComparison.Ordinal) == true)
            .Where(a => a.GetName().Name?.EndsWith(".Tests", StringComparison.Ordinal) != true)
            .Distinct()
            .ToArray();

        Assert.Contains(assemblies, a => a == typeof(IWriteExecutor).Assembly);
        Assert.Contains(assemblies, a => a == typeof(ReviewGate).Assembly);

        var offenders = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type != typeof(IWriteExecutor)
                    && typeof(IWriteExecutor).IsAssignableFrom(type)
                    && !IsTheConformanceTripwire(type))
                {
                    offenders.Add($"{assembly.GetName().Name}: {type.FullName} implements the port");
                }

                const BindingFlags All =
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                foreach (var field in type.GetFields(All))
                {
                    if (field.FieldType == typeof(IWriteExecutor) && !IsTheConformanceTripwire(type))
                        offenders.Add($"{type.FullName}.{field.Name} holds the port");
                }

                foreach (var method in type.GetMethods(All).Where(m => m.DeclaringType == type))
                {
                    if (IsTheConformanceTripwire(type)) continue;
                    if (method.ReturnType == typeof(IWriteExecutor))
                        offenders.Add($"{type.FullName}.{method.Name} returns the port");
                    if (method.GetParameters().Any(p => p.ParameterType == typeof(IWriteExecutor)))
                        offenders.Add($"{type.FullName}.{method.Name} takes the port");
                }

                foreach (var ctor in type.GetConstructors(All))
                {
                    if (IsTheConformanceTripwire(type)) continue;
                    if (ctor.GetParameters().Any(p => p.ParameterType == typeof(IWriteExecutor)))
                        offenders.Add($"{type.FullName}'s constructor takes the port");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "AZ-7: no shipped type may hold, take, return or implement IWriteExecutor. Found:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>Whether <paramref name="type"/> is the compliance harness's GT-6 tripwire.</summary>
    private static bool IsTheConformanceTripwire(Type type) =>
        type.Assembly.GetName().Name == "Affiant.Testing.ComplianceHarness"
        && type.FullName?.Contains(".Conformance.", StringComparison.Ordinal) == true;

    /// <summary>
    /// The gate's own surface, stated positively: the only method that moves a row to an executed
    /// write is the host's report, and it is named as such.
    /// </summary>
    [Fact]
    public void TheGateExposesNoExecuteMethod_OnlyTheHostsReport()
    {
        var names = typeof(ReviewGate)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(ReviewGate))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(ReviewGate.MarkExecutedAsync), names);
        Assert.DoesNotContain("ExecuteAsync", names);
        Assert.DoesNotContain("Execute", names);
    }

    /// <summary>The repository root, found by walking up to the solution file the test was built from.</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Affiant.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
