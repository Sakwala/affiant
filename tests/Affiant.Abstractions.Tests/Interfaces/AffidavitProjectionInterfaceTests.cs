namespace Affiant.Abstractions.Tests.Interfaces;

using System.Reflection;
using Affiant.Abstractions.Interfaces;
using Xunit;

public class AffidavitProjectionInterfaceTests
{
    [Fact]
    public void Interface_HasOneProperty_EntityType()
    {
        var props = typeof(IAffidavitProjection).GetProperties();
        Assert.Single(props);
        Assert.Equal("EntityType", props[0].Name);
        Assert.Equal(typeof(string), props[0].PropertyType);
    }

    [Fact]
    public void Interface_HasOneMethod_Project()
    {
        var methods = typeof(IAffidavitProjection)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();
        Assert.Single(methods);
        Assert.Equal("Project", methods[0].Name);
    }

    [Fact]
    public void Project_HasThreeParameters()
    {
        var method = typeof(IAffidavitProjection).GetMethod("Project");
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal("fabric", parameters[0].Name);
        Assert.Equal("operationType", parameters[1].Name);
        Assert.Equal("warnings", parameters[2].Name);
    }

    [Fact]
    public void Project_ReturnsAffidavit()
    {
        var method = typeof(IAffidavitProjection).GetMethod("Project");
        Assert.NotNull(method);
        Assert.Equal(typeof(Affiant.Abstractions.Models.Affidavit), method.ReturnType);
    }

    [Fact]
    public void Fabric_Parameter_IsIContextFabric()
    {
        var method = typeof(IAffidavitProjection).GetMethod("Project");
        Assert.NotNull(method);
        var fabricParam = method.GetParameters()[0];
        Assert.Equal(typeof(IContextFabric), fabricParam.ParameterType);
    }
}
