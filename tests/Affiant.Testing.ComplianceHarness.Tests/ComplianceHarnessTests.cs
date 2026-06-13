namespace Affiant.Testing.ComplianceHarness.Tests;

using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class ComplianceHarnessTests
{
    [Fact]
    public void Verify_StubThrows()
    {
        var services = new ServiceCollection();

        Assert.Throws<NotImplementedException>(() => ComplianceHarness.Verify(services));
    }
}
