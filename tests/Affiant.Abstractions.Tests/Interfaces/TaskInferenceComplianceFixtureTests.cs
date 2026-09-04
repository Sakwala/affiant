namespace Affiant.Abstractions.Tests.Interfaces;

using System.Reflection;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Xunit;

public class TaskInferenceComplianceFixtureTests
{
    [Fact]
    public void Interface_HasTwoProperties()
    {
        var props = typeof(ITaskInferenceComplianceFixture).GetProperties();
        Assert.Equal(2, props.Length);
        Assert.Contains(props, p => p.Name == "Strategy");
        Assert.Contains(props, p => p.Name == "Cases");
    }

    [Fact]
    public void Strategy_PropertyType_IsSystemType()
    {
        var prop = typeof(ITaskInferenceComplianceFixture).GetProperty("Strategy");
        Assert.NotNull(prop);
        Assert.Equal(typeof(Type), prop.PropertyType);
    }

    [Fact]
    public void Cases_PropertyType_IsEnumerableOfInferenceFixtureCase()
    {
        var prop = typeof(ITaskInferenceComplianceFixture).GetProperty("Cases");
        Assert.NotNull(prop);
        Assert.Equal(typeof(IEnumerable<InferenceFixtureCase>), prop.PropertyType);
    }

    [Fact]
    public void InferenceFixtureCase_HasFivePositionalParameters()
    {
        var props = typeof(InferenceFixtureCase).GetProperties();
        Assert.Equal(5, props.Length);
        Assert.Contains(props, p => p.Name == "Name");
        Assert.Contains(props, p => p.Name == "History");
        Assert.Contains(props, p => p.Name == "Arguments");
        Assert.Contains(props, p => p.Name == "Assertion");
        // The entity an update-shaped case targets; null (and defaulted) for a create.
        Assert.Contains(props, p => p.Name == "EntityId");
    }

    [Fact]
    public void InferenceFixtureCase_Assertion_IsFuncOfAffidavitBool()
    {
        var prop = typeof(InferenceFixtureCase).GetProperty("Assertion");
        Assert.NotNull(prop);
        Assert.Equal(typeof(Func<Affidavit, bool>), prop.PropertyType);
    }
}
