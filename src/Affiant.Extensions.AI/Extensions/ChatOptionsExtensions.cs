namespace Affiant.Extensions.AI.Extensions;

using Affiant.Abstractions.Interfaces;
using Affiant.Core.Services;
using Affiant.Extensions.AI.Filters;
using Affiant.Extensions.AI.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The single blessed way to attach Affiant to a Microsoft.Extensions.AI chat call — the
/// counterpart of <c>Affiant.AgentFramework</c>'s <c>AIAgent.WithAffiant</c> and of
/// <c>Affiant.SemanticKernel</c>'s plugin registration.
///
/// <para>
/// Wiring produces a new <see cref="ChatOptions"/> instance whose <see cref="ChatOptions.Tools"/> are
/// the wrapped functions. The pre-wrap options object silently bypasses Affiant if a host keeps
/// using it, so this method returns the wired instance and hosts must use only that.
/// </para>
///
/// <para>
/// <b>Why <see cref="ChatOptions"/> is the one entry point.</b> It is where the tool list lives, and
/// routing every host through it means the hosted-tool coverage audit has exactly one place to run
/// with nothing to miss. A "wrap this bare list of tools" overload was considered and rejected: it
/// would let a host wrap its <see cref="AIFunction"/>s and then attach unaudited hosted tools to
/// <see cref="ChatOptions"/> afterwards, reopening precisely the coverage hole the audit exists to
/// close.
/// </para>
/// </summary>
public static class ChatOptionsExtensions
{
    /// <summary>
    /// Registers <paramref name="catalog"/>'s descriptors with <see cref="IAffiantToolRegistry"/>,
    /// runs the hosted-tool coverage audit over the resulting tool list (see
    /// <c>Affiant.Extensions.AI.Validation.HostedToolAudit</c>), wraps every client-invoked
    /// <see cref="AIFunction"/> so it runs the neutral tool-invocation pipeline, and returns a new
    /// <see cref="ChatOptions"/> carrying the wrapped tools.
    ///
    /// <para>
    /// Requires <c>services.AddAffiantCore()</c> and <c>services.AddAffiantExtensionsAI()</c> to have
    /// been called first. The chat client this is used with must have
    /// <c>UseFunctionInvocation()</c> in its pipeline — that is the client that runs the tool loop
    /// and publishes the per-call context Affiant's wrapper reads.
    /// </para>
    ///
    /// <para>
    /// Tool-list composition, matching the Microsoft Agent Framework adapter's behaviour exactly:
    /// every <see cref="AIFunction"/> already on <paramref name="options"/> is wrapped (whether or not
    /// it came from the catalog), every non-<see cref="AIFunction"/> tool is audited and passed
    /// through untouched, and any catalog function not already present by name is appended, wrapped.
    /// Order is preserved: existing tools first, in place, then the catalog's additions.
    /// </para>
    ///
    /// <para>
    /// <b>Set <see cref="ChatOptions.ConversationId"/> on the returned options.</b> It is not
    /// decorative here. Affiant dedups task inference per (conversation, tool, turn), and with no
    /// conversation id the key falls back to the identity of the conversation-state object — which at
    /// this seam is process-global, because <see cref="FunctionInvokingChatClient"/> hands the
    /// pipeline the provider the <see cref="ChatClientBuilder"/> was built from (the application
    /// root) rather than a per-conversation scope. Every conversation then shares one key and the
    /// second and later ones <em>silently</em> skip write-tool inference. See
    /// <c>AffiantDelegatingAIFunction</c>'s KNOWN LIMITATION note, the package README, and
    /// <c>Affiant.Extensions.AI.Tests.Filters.ConversationScopeBleedAtTheSeamTests</c>.
    /// </para>
    ///
    /// <para>
    /// <b>The double-wrap refusal here is the early half of two.</b> This check is a top-level type
    /// test over the tool list, so it sees an Affiant wrapper only when nothing is layered over it.
    /// <c>AffiantDelegatingAIFunction</c> carries the backstop: an invoke-time re-entrancy guard that
    /// refuses a nested onion whatever hides it. This one is kept because it fails at wire-up, before
    /// any turn runs, and leaves the registry untouched.
    /// </para>
    /// </summary>
    /// <param name="options">The host's chat options. Not mutated; a copy is returned.</param>
    /// <param name="services">The application's service provider.</param>
    /// <param name="catalog">The tool catalog whose descriptors and functions Affiant should govern.</param>
    /// <returns>A new <see cref="ChatOptions"/> whose tools run through Affiant. Use only this instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Affiant is not wired up; or the tool list contains an unacknowledged hosted tool; or a tool is
    /// already wrapped by this adapter (double-wrap guard).
    /// </exception>
    public static ChatOptions WithAffiant(
        this ChatOptions options,
        IServiceProvider services,
        AffiantToolCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        var registry = services.GetService<IAffiantToolRegistry>()
            ?? throw new InvalidOperationException(
                "Affiant.Extensions.AI: IAffiantToolRegistry is not registered. " +
                "Call services.AddAffiantCore() before chatOptions.WithAffiant().");

        var pipeline = services.GetService<ToolInvocationPipeline>()
            ?? throw new InvalidOperationException(
                "Affiant.Extensions.AI: ToolInvocationPipeline is not registered. " +
                "Call services.AddAffiantCore() before chatOptions.WithAffiant().");

        var adapterOptions = services.GetService<ExtensionsAIOptions>()
            ?? throw new InvalidOperationException(
                "Affiant.Extensions.AI: ExtensionsAIOptions is not registered. " +
                "Call services.AddAffiantExtensionsAI() before chatOptions.WithAffiant().");

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Affiant.Extensions.AI")
            ?? NullLogger.Instance;

        var existing = options.Tools is null ? [] : options.Tools.ToList();

        // Double-wrap guard (design decision 6) — before anything else, so a refused wiring is a
        // pure no-op. Covers both halves of the mistake: options already wired, and a catalog whose
        // functions were wrapped at another wiring site and reused here.
        GuardAgainstDoubleWrap(existing.Concat(catalog.Functions));

        // Audit before any registry mutation: a refused wiring (unacknowledged hosted tool) must
        // leave the singleton registry untouched, so a corrected retry does not die with
        // "already registered" from AffiantToolRegistry.Register.
        HostedToolAudit.Run(existing, adapterOptions, logger);

        foreach (var descriptor in catalog.Descriptors)
            registry.Register(descriptor);

        var wired = new List<AITool>(existing.Count + catalog.Functions.Count);
        var wrappedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in existing)
        {
            if (tool is AIFunction function)
            {
                wired.Add(new AffiantDelegatingAIFunction(function, pipeline, registry));
                wrappedNames.Add(function.Name);
            }
            else
            {
                // Hosted/provider-executed marker: audited above, nothing to wrap. Passed through so
                // the host's provider still sees it.
                wired.Add(tool);
            }
        }

        foreach (var function in catalog.Functions)
        {
            if (!wrappedNames.Add(function.Name)) continue;
            wired.Add(new AffiantDelegatingAIFunction(function, pipeline, registry));
        }

        // Clone rather than mutate: a host that keeps the pre-wrap options must not silently get
        // Affiant coverage it did not ask for, and a host that keeps it deliberately (for a
        // separate, un-governed call) must not have its tool list rewritten underneath it.
        var wiredOptions = options.Clone();
        wiredOptions.Tools = wired;
        return wiredOptions;
    }

    private static void GuardAgainstDoubleWrap(IEnumerable<AITool> tools)
    {
        var alreadyWrapped = tools
            .OfType<IAffiantWrappedFunction>()
            .Select(w => w.AffiantInnerFunction.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (alreadyWrapped.Count == 0) return;

        throw new InvalidOperationException(
            "Affiant.Extensions.AI: WithAffiant refuses to wire up tools that Affiant already governs: " +
            $"{string.Join(", ", alreadyWrapped)}. Wrapping a tool twice runs the neutral filter onion " +
            "twice for one logical tool call, which double-tags provenance, fires task inference twice, " +
            "and files the same write proposal onto the docket twice — a silent semantic corruption, not " +
            "an error anything downstream would report. Call WithAffiant exactly once per ChatOptions, on " +
            "the unwrapped catalog, and use only the ChatOptions it returns. If the tools reached here " +
            "from another wiring site, build a fresh AffiantToolCatalog instead of sharing the wired one. " +
            "The same one-adapter-per-catalog rule applies across adapters — never wire both " +
            "Affiant.Extensions.AI and Affiant.AgentFramework over the same tools. This check cannot see " +
            "that case, nor any wrapper hidden behind host middleware; AffiantDelegatingAIFunction's " +
            "invoke-time re-entrancy guard catches both, one call later.");
    }
}
