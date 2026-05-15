namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface ITaskInferenceComplianceFixture
{
    Type Strategy { get; }
    IEnumerable<InferenceFixtureCase> Cases { get; }
}
