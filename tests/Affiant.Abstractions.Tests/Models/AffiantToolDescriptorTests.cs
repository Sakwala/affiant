using System.Text.Json;
using System.Text.Json.Serialization;
using Affiant.Abstractions.Models;
using Xunit;

namespace Affiant.Abstractions.Tests.Models;

public sealed class AffiantToolDescriptorTests
{
    [Fact]
    public void Descriptor_RecordEquality_HoldsForIdenticalFields()
    {
        var a = new AffiantToolDescriptor("CreateFoo", "FooPlugin", Operation.WriteCreate, "Foo", typeof(AffiantToolDescriptorTests));
        var b = new AffiantToolDescriptor("CreateFoo", "FooPlugin", Operation.WriteCreate, "Foo", typeof(AffiantToolDescriptorTests));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Descriptor_RecordEquality_FailsWhenAnyFieldDiffers()
    {
        var baseline = new AffiantToolDescriptor("CreateFoo", "FooPlugin", Operation.WriteCreate, "Foo", typeof(AffiantToolDescriptorTests));
        Assert.NotEqual(baseline, baseline with { FunctionName = "Other" });
        Assert.NotEqual(baseline, baseline with { PluginName = "Other" });
        Assert.NotEqual(baseline, baseline with { Operation = Operation.WriteUpdate });
        Assert.NotEqual(baseline, baseline with { EntityType = "Other" });
        Assert.NotEqual(baseline, baseline with { InferenceStrategy = typeof(OperationTests) });
    }

    [Fact]
    public void Descriptor_JsonRoundTrip_PreservesAllFields_ForWriteCreate()
    {
        // System.Text.Json cannot serialize System.Type by default (security-by-default: deserializing
        // arbitrary type names enables RCE). A test-local converter encodes Type as AssemblyQualifiedName.
        // See Story 15.1 Gotcha 1 for the full rationale; the converter is intentionally test-only.
        var options = BuildOptionsWithTypeConverter();
        var descriptor = new AffiantToolDescriptor("CreateFoo", "FooPlugin", Operation.WriteCreate, "Foo", typeof(AffiantToolDescriptorTests));
        var json = JsonSerializer.Serialize(descriptor, options);
        var deserialized = JsonSerializer.Deserialize<AffiantToolDescriptor>(json, options)!;
        Assert.Equal(descriptor.FunctionName,       deserialized.FunctionName);
        Assert.Equal(descriptor.PluginName,         deserialized.PluginName);
        Assert.Equal(descriptor.Operation,          deserialized.Operation);
        Assert.Equal(descriptor.EntityType,         deserialized.EntityType);
        Assert.Equal(descriptor.InferenceStrategy,  deserialized.InferenceStrategy);
    }

    [Fact]
    public void Descriptor_JsonRoundTrip_PreservesNullFields_ForReadQuery()
    {
        var options = BuildOptionsWithTypeConverter();
        var descriptor = new AffiantToolDescriptor("GetFoo", null, Operation.ReadQuery, null, null);
        var json = JsonSerializer.Serialize(descriptor, options);
        var deserialized = JsonSerializer.Deserialize<AffiantToolDescriptor>(json, options)!;
        Assert.Equal("GetFoo",          deserialized.FunctionName);
        Assert.Null(deserialized.PluginName);
        Assert.Equal(Operation.ReadQuery, deserialized.Operation);
        Assert.Null(deserialized.EntityType);
        Assert.Null(deserialized.InferenceStrategy);
    }

    [Fact]
    public void Descriptor_AllowsSemanticallyInvalidConstruction()
    {
        // WriteCreate with no EntityType and no InferenceStrategy is semantically invalid, but the
        // record enforces no constraint — semantic validation is the startup validator's job (Story 15.5).
        // PRD Task 1.3 Verification line 3: "allowed at compile time but fails the startup validator at runtime."
        var descriptor = new AffiantToolDescriptor("X", null, Operation.WriteCreate, null, null);
        Assert.Equal("X", descriptor.FunctionName);
    }

    private static JsonSerializerOptions BuildOptionsWithTypeConverter()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new TypeJsonConverter());
        return options;
    }

    // Test-local only — not production code. See Descriptor_JsonRoundTrip_PreservesAllFields_ForWriteCreate.
    private sealed class TypeJsonConverter : JsonConverter<Type>
    {
        public override Type? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var name = reader.GetString();
            return name is null ? null : Type.GetType(name);
        }

        public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.AssemblyQualifiedName);
    }
}
