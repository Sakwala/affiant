namespace Affiant.Core.Tests.Invariants.TestFixtures;

using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;

internal sealed class FakeWorkOrderComplianceFixture : ITaskInferenceComplianceFixture
{
    public Type Strategy => typeof(FakeWorkOrderStrategy);

    public IEnumerable<InferenceFixtureCase> Cases
    {
        get
        {
            IReadOnlyList<AffiantChatMessage> history =
            [
                new AffiantChatMessage("user",
                    "Create a work order to replace the engine on aircraft A7-BCA, priority High."),
            ];

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
