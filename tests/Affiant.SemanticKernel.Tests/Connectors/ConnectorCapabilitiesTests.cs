using Affiant.SemanticKernel.Connectors;
using Affiant.SemanticKernel.Connectors.Capabilities;
using Xunit;

namespace Affiant.SemanticKernel.Tests.Connectors;

public class ConnectorCapabilitiesTests
{
    [Fact]
    public void OpenAiCapabilities_SupportsAllFeatures()
    {
        var caps = new OpenAiCapabilities();
        Assert.True(caps.SupportsAutoFunctionInvocationFilter);
        Assert.True(caps.SupportsStreamingFunctionCalls);
        Assert.True(caps.SupportsStructuredOutput);
        Assert.True(caps.SupportsParallelToolCalls);
    }

    [Fact]
    public void AzureOpenAiCapabilities_SupportsAllFeatures()
    {
        var caps = new AzureOpenAiCapabilities();
        Assert.True(caps.SupportsAutoFunctionInvocationFilter);
        Assert.True(caps.SupportsStreamingFunctionCalls);
        Assert.True(caps.SupportsStructuredOutput);
        Assert.True(caps.SupportsParallelToolCalls);
    }

    [Fact]
    public void GoogleGeminiCapabilities_NoStreamingFunctionCalls()
    {
        var caps = new GoogleGeminiCapabilities();
        Assert.True(caps.SupportsAutoFunctionInvocationFilter);
        Assert.False(caps.SupportsStreamingFunctionCalls);
        Assert.True(caps.SupportsStructuredOutput);
        Assert.True(caps.SupportsParallelToolCalls);
    }

    [Fact]
    public void OllamaCapabilities_LimitedCapabilities()
    {
        var caps = new OllamaCapabilities();
        Assert.True(caps.SupportsAutoFunctionInvocationFilter);
        Assert.False(caps.SupportsStreamingFunctionCalls);
        Assert.False(caps.SupportsStructuredOutput);
        Assert.False(caps.SupportsParallelToolCalls);
    }

    [Fact]
    public void OpenAiCompatibleCapabilities_SupportsAllFeatures()
    {
        var caps = new OpenAiCompatibleCapabilities();
        Assert.True(caps.SupportsAutoFunctionInvocationFilter);
        Assert.True(caps.SupportsStreamingFunctionCalls);
        Assert.True(caps.SupportsStructuredOutput);
        Assert.True(caps.SupportsParallelToolCalls);
    }

    [Fact]
    public void CapabilityRegistry_ResolvesAllKnownProviders()
    {
        var registry = new CapabilityRegistry();
        Assert.True(registry.Resolve("openai").SupportsAutoFunctionInvocationFilter);
        Assert.True(registry.Resolve("azure-openai").SupportsAutoFunctionInvocationFilter);
        Assert.True(registry.Resolve("google").SupportsAutoFunctionInvocationFilter);
        Assert.True(registry.Resolve("ollama").SupportsAutoFunctionInvocationFilter);
        Assert.True(registry.Resolve("openai-compatible").SupportsAutoFunctionInvocationFilter);
    }

    [Fact]
    public void CapabilityRegistry_IsCaseInsensitive()
    {
        var registry = new CapabilityRegistry();
        Assert.True(registry.Resolve("OpenAI").SupportsAutoFunctionInvocationFilter);
        Assert.True(registry.Resolve("GOOGLE").SupportsAutoFunctionInvocationFilter);
        Assert.True(registry.Resolve("Ollama").SupportsAutoFunctionInvocationFilter);
    }

    [Fact]
    public void CapabilityRegistry_ThrowsForUnknownProvider()
    {
        var registry = new CapabilityRegistry();
        Assert.Throws<KeyNotFoundException>(() => registry.Resolve("unknown-provider"));
    }
}
