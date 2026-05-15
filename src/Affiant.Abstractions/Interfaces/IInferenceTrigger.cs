namespace Affiant.Abstractions.Interfaces;

using Affiant.Abstractions.Models;

public interface IInferenceTrigger
{
    bool ShouldRun(InferenceTriggerContext context);
}
