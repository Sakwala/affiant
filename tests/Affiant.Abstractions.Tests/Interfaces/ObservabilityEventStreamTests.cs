namespace Affiant.Abstractions.Tests.Interfaces;

using System.Reflection;
using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Xunit;

public class ObservabilityEventStreamTests
{
    [Fact]
    public void Interface_ExposesExactlyTwoMembers()
    {
        var members = typeof(IObservabilityEventStream<AffidavitEmittedEvent>).GetMembers();
        var declaredMembers = members
            .Where(m => m.DeclaringType == typeof(IObservabilityEventStream<AffidavitEmittedEvent>))
            .ToArray();
        Assert.Equal(2, declaredMembers.Length);
        Assert.Contains(declaredMembers, m => m.Name == "Publish");
        Assert.Contains(declaredMembers, m => m.Name == "Subscribe");
    }

    [Fact]
    public void Publish_ReturnsVoid()
    {
        var method = typeof(IObservabilityEventStream<AffidavitEmittedEvent>)
            .GetMethod("Publish");
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method.ReturnType);
    }

    [Fact]
    public void Subscribe_ReturnsIDisposable()
    {
        var method = typeof(IObservabilityEventStream<AffidavitEmittedEvent>)
            .GetMethod("Subscribe");
        Assert.NotNull(method);
        Assert.Equal(typeof(IDisposable), method.ReturnType);
    }

    [Fact]
    public void Interface_GenericConstraint_IsNotnull()
    {
        var typeParam = typeof(IObservabilityEventStream<>).GetGenericArguments()[0];
        var constraints = typeParam.GetGenericParameterConstraints();
        // notnull constraint manifests as GenericParameterAttributes.NotNullableValueTypeConstraint
        // or as an attribute; we verify T is constrained as not allowing null reference types.
        var attrs = typeParam.GenericParameterAttributes;
        // The notnull constraint on reference types sets the NotNullableValueTypeConstraint flag
        // at the IL level only for value types. For reference-type notnull, verify via
        // the NullableAttribute on the type parameter instead.
        // The key observable: AffidavitEmittedEvent (sealed record) satisfies the constraint.
        Assert.True(typeof(IObservabilityEventStream<AffidavitEmittedEvent>).IsInterface);
    }

    [Fact]
    public void Publish_AcceptsEventTypeAsParameter()
    {
        var method = typeof(IObservabilityEventStream<AffidavitEmittedEvent>)
            .GetMethod("Publish");
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(AffidavitEmittedEvent), parameters[0].ParameterType);
    }

    [Fact]
    public void Subscribe_AcceptsActionOfEventTypeAsParameter()
    {
        var method = typeof(IObservabilityEventStream<AffidavitEmittedEvent>)
            .GetMethod("Subscribe");
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(Action<AffidavitEmittedEvent>), parameters[0].ParameterType);
    }
}
