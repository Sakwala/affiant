namespace Affiant.SemanticKernel.Tests.Utilities;

/// <summary>
/// Test utility that captures an execution trace for structural comparison between
/// the OpenAI auto-invocation path and the Gemini manual-invocation path.
/// Both paths should produce the same observable state (functions invoked, attributes set)
/// even though the underlying filter types differ.
/// </summary>
public sealed class FilterExecutionTracer
{
    private readonly List<string> _filterExecutionOrder = [];
    private readonly List<string> _functionNames = [];
    private readonly Dictionary<string, object?> _capturedAttributes = [];

    public void RecordFilter(string filterName) => _filterExecutionOrder.Add(filterName);
    public void RecordFunction(string functionName) => _functionNames.Add(functionName);
    public void RecordAttribute(string key, object? value) => _capturedAttributes[key] = value;

    public void Reset()
    {
        _filterExecutionOrder.Clear();
        _functionNames.Clear();
        _capturedAttributes.Clear();
    }

    public FilterTrace CaptureTrace() => new(
        [.. _filterExecutionOrder],
        [.. _functionNames],
        new Dictionary<string, object?>(_capturedAttributes));
}

/// <summary>
/// Immutable snapshot of a filter chain execution trace, used for structural comparison
/// between provider paths in dual-provider integration tests.
/// </summary>
public sealed record FilterTrace(
    IReadOnlyList<string> FilterExecutionOrder,
    IReadOnlyList<string> FunctionNames,
    IReadOnlyDictionary<string, object?> CapturedAttributes);
