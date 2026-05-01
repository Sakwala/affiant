namespace Affiant.Core.Services;

/// <summary>
/// Configuration options for the Affiant.SemanticKernel DI registration.
/// Hosts configure these via the <c>AddAffiantSemanticKernel</c> callback at startup.
/// </summary>
public sealed class SemanticKernelOptions
{
    /// <summary>
    /// Primary LLM provider name (e.g., "openai", "azure-openai", "google").
    /// Used to select connector capabilities and log startup diagnostics.
    /// Default: "AzureOpenAI".
    /// </summary>
    public string PrimaryProvider { get; set; } = "AzureOpenAI";

    /// <summary>
    /// Fallback LLM provider name when the primary is unavailable (e.g., "google").
    /// Used by the host's failover logic (ChatHub) in conjunction with ProviderPair.
    /// Default: "Gemini".
    /// </summary>
    public string FallbackProvider { get; set; } = "Gemini";

    /// <summary>
    /// When true, SK's IAutoFunctionInvocationFilter chain is active (default: true).
    /// Set to false when the primary provider does not support auto-function invocation.
    /// </summary>
    public bool EnableAutoFunctionInvocation { get; set; } = true;

    /// <summary>
    /// When true, ManualToolInvoker is the active fallback path for providers that
    /// do not support SK's auto-function invocation natively (default: true).
    /// </summary>
    public bool EnableManualInvocationFallback { get; set; } = true;

    /// <summary>
    /// Maximum invocation retries before giving up. Used by the host's retry logic.
    /// Default: 3.
    /// </summary>
    public int MaxAutoInvocationRetries { get; set; } = 3;

    /// <summary>
    /// When true, log filter-chain execution at Debug level (default: false).
    /// Enable in Development to troubleshoot filter registration order.
    /// </summary>
    public bool EnableFilterLogging { get; set; } = false;
}
