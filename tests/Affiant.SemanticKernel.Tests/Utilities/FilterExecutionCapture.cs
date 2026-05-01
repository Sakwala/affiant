namespace Affiant.SemanticKernel.Tests.Utilities;

/// <summary>
/// Test utility that tracks which filters and components executed during a kernel invocation.
/// Register as a singleton in the test DI container; spy filters record themselves here via
/// <see cref="RecordFilter"/>. Use <see cref="Clear"/> between test cases when the same
/// instance is shared across multiple invocations.
/// </summary>
public sealed class FilterExecutionCapture
{
    private readonly List<string> _executedFilters = [];
    private readonly List<string> _executedComponents = [];

    public IReadOnlyList<string> ExecutedFilters => _executedFilters.AsReadOnly();
    public IReadOnlyList<string> ExecutedComponents => _executedComponents.AsReadOnly();

    public void RecordFilter(string filterName) => _executedFilters.Add(filterName);
    public void RecordComponent(string componentName) => _executedComponents.Add(componentName);

    public void Clear()
    {
        _executedFilters.Clear();
        _executedComponents.Clear();
    }
}
