namespace Affiant.Core.Tests.Invariants.TestFixtures;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

internal sealed class FakeWorkOrderStrategy : ITaskInferenceStrategy
{
    public string EntityName => "WorkOrder";

    public IReadOnlyList<TaskInferenceField> Fields =>
    [
        new TaskInferenceField("Title", "string", "Work order title"),
        new TaskInferenceField("Priority", "string", "Priority level"),
        new TaskInferenceField("AircraftId", "string", "Aircraft identifier"),
    ];

    public double? MinimumConfidenceThreshold => null;
}
