using Affiant.SemanticKernel.Connectors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Xunit;

namespace Affiant.SemanticKernel.Tests.Connectors;

public class ManualToolInvokerTests
{
    [Fact]
    public async Task InvokesRegisteredFunction_AndReturnsResult()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var plugin = KernelPluginFactory.CreateFromFunctions("TestPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => 42, "GetAnswer")]);
        kernel.Plugins.Add(plugin);

        var invoker = new ManualToolInvoker(NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent(
            functionName: "GetAnswer",
            pluginName: "TestPlugin",
            id: "call-1");

        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        Assert.Equal("42", result.Result?.ToString());
        Assert.Equal("call-1", result.CallId);
    }

    [Fact]
    public async Task ReturnsErrorResult_WhenFunctionNotFound()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var invoker = new ManualToolInvoker(NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent(
            functionName: "Missing",
            pluginName: "NonExistent",
            id: "call-2");

        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        var resultStr = result.Result?.ToString() ?? string.Empty;
        Assert.Contains("FUNCTION_NOT_FOUND", resultStr);
        Assert.Equal("call-2", result.CallId);
    }

    [Fact]
    public async Task PreservesPluginName_InResult()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var plugin = KernelPluginFactory.CreateFromFunctions("MyPlugin",
            [KernelFunctionFactory.CreateFromMethod(() => "hello", "Greet")]);
        kernel.Plugins.Add(plugin);

        var invoker = new ManualToolInvoker(NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent(
            functionName: "Greet",
            pluginName: "MyPlugin",
            id: "call-3");

        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        Assert.Equal("MyPlugin", result.PluginName);
        Assert.Equal("Greet", result.FunctionName);
    }

    [Fact]
    public async Task PassesArguments_ToFunction()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var plugin = KernelPluginFactory.CreateFromFunctions("MathPlugin",
            [KernelFunctionFactory.CreateFromMethod((int x) => x * 2, "Double")]);
        kernel.Plugins.Add(plugin);

        var invoker = new ManualToolInvoker(NullLogger<ManualToolInvoker>.Instance);
        var call = new FunctionCallContent(
            functionName: "Double",
            pluginName: "MathPlugin",
            id: "call-4",
            arguments: new KernelArguments { ["x"] = "7" });

        var result = await invoker.CaptureAndInvokeAsync(call, kernel, CancellationToken.None);

        Assert.Equal("14", result.Result?.ToString());
    }
}
