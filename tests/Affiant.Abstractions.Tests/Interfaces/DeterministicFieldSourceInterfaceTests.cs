namespace Affiant.Abstractions.Tests.Interfaces;

using System.Reflection;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Xunit;

#pragma warning disable CS0618 // Testing the soft-deprecated IDeterministicFieldSource, kept fully functional (P2 area-1 wave)

public class DeterministicFieldSourceInterfaceTests
{
    [Fact]
    public void Interface_HasOneProperty_FieldName()
    {
        var props = typeof(IDeterministicFieldSource).GetProperties();
        Assert.Single(props);
        Assert.Equal("FieldName", props[0].Name);
        Assert.Equal(typeof(string), props[0].PropertyType);
    }

    [Fact]
    public void Interface_HasOneMethod_Resolve()
    {
        var methods = typeof(IDeterministicFieldSource)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToArray();
        Assert.Single(methods);
        Assert.Equal("Resolve", methods[0].Name);
    }

    [Fact]
    public void Resolve_HasOneParameter_Fabric()
    {
        var method = typeof(IDeterministicFieldSource).GetMethod("Resolve");
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(IContextFabric), parameters[0].ParameterType);
    }

    [Fact]
    public void Resolve_ReturnType_IsNullableProvenanceTag()
    {
        var method = typeof(IDeterministicFieldSource).GetMethod("Resolve");
        Assert.NotNull(method);
        // Return type is ProvenanceTag? — the underlying type is ProvenanceTag (sealed record)
        // and nullability is expressed as a nullable reference type annotation.
        Assert.Equal(typeof(ProvenanceTag), method.ReturnType);

        var nullabilityCtx = new NullabilityInfoContext();
        var nullabilityInfo = nullabilityCtx.Create(method.ReturnParameter);
        Assert.Equal(NullabilityState.Nullable, nullabilityInfo.WriteState);
    }
}
