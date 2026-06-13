namespace Affiant.Core.Tests.Invariants.TestFixtures;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Microsoft.SemanticKernel.ChatCompletion;

internal sealed class FakeWorkOrderComplianceFixture : ITaskInferenceComplianceFixture
{
    public Type Strategy => typeof(FakeWorkOrderStrategy);

    public IEnumerable<InferenceFixtureCase> Cases
    {
        get
        {
            var history = new ChatHistory();
            history.AddUserMessage(
                "Create a work order to replace the engine on aircraft A7-BCA, priority High.");

            yield return new InferenceFixtureCase(
                Name: "happy_path_create_work_order",
                History: history,
                Arguments: new Dictionary<string, object?>
                {
                    ["title"] = "Replace aircraft engine",
                    ["priority"] = "High",
                    ["aircraftId"] = "A7-BCA",
                },
                Assertion: affidavit => affidavit.Fields.Length > 0);
        }
    }
}
