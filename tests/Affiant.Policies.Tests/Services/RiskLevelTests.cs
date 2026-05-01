namespace Affiant.Policies.Tests.Services;

using Affiant.Policies.Services;
using Xunit;

public class RiskLevelTests
{
    [Fact]
    public void RiskLevel_values_match_R1_R2_R3_schema()
    {
        Assert.Equal(1, (int)RiskLevel.Low);
        Assert.Equal(2, (int)RiskLevel.Medium);
        Assert.Equal(3, (int)RiskLevel.High);
    }
}
