using System.Text.Json;
using Affiant.Abstractions.Models;
using Xunit;

namespace Affiant.Abstractions.Tests.Models;

public sealed class OperationTests
{
    [Fact]
    public void WellKnownFactories_HaveExpectedKindStrings()
    {
        Assert.Equal("ReadQuery",   Operation.ReadQuery.Kind);
        Assert.Equal("WriteCreate", Operation.WriteCreate.Kind);
        Assert.Equal("WriteUpdate", Operation.WriteUpdate.Kind);
        Assert.Equal("WriteDelete", Operation.WriteDelete.Kind);
    }

    [Fact]
    public void HostConstructed_EqualsFrameworkFactory_WhenKindMatches()
    {
        var hostInstance = new Operation("WriteCreate");
        Assert.True(hostInstance.Equals(Operation.WriteCreate));
    }

    [Fact]
    public void OperationsWithDifferentKinds_AreNotEqual()
    {
        Assert.False(Operation.WriteCreate.Equals(Operation.WriteUpdate));
    }

    [Fact]
    public void Operation_JsonRoundTrip_PreservesKind()
    {
        var json = JsonSerializer.Serialize(Operation.WriteCreate);
        var deserialized = JsonSerializer.Deserialize<Operation>(json);
        Assert.Equal("WriteCreate", deserialized!.Kind);
    }

    [Fact]
    public void HostDefinedKind_RoundTripsCleanly()
    {
        var op = new Operation("WriteUpsert");
        var json = JsonSerializer.Serialize(op);
        var deserialized = JsonSerializer.Deserialize<Operation>(json);
        Assert.Equal("WriteUpsert", deserialized!.Kind);
    }
}
