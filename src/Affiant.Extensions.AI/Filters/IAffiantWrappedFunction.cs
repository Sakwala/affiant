namespace Affiant.Extensions.AI.Filters;

using Microsoft.Extensions.AI;

/// <summary>
/// Marker for an <see cref="AIFunction"/> that already runs Affiant's tool-invocation pipeline.
///
/// <para>
/// <b>Why a marker exists at all (design brief
/// <c>affiant-chancery/docs/overnight-mission-2026-08-20/meai-adapter-design.md</c>, decision 6).</b>
/// Affiant's neutral pipeline is not idempotent: <c>ToolArgumentCaptureFilter</c> writes a provenance
/// chain, <c>InferenceTriggerFilter</c> fires an inference call, <c>ReviewGateFilter</c> files a write
/// proposal onto the docket. Running the onion twice for one logical tool call double-files, double-tags
/// and double-infers — a semantic corruption, not a crash, so nothing downstream would report it.
/// Neither Microsoft.Extensions.AI nor the Microsoft Agent Framework offers any way to notice that a
/// function is already intercepted (seam probe <c>research/meai-seam-probe.md</c> §5, "no detection
/// mechanism found"), so Affiant supplies its own.
/// </para>
///
/// <para>
/// <b>What the marker does and does not catch.</b>
/// <c>WithAffiant</c> refuses at wire-up when any tool it is asked to wrap already implements this
/// interface — i.e. this package wrapping its own output a second time, the common mistake (calling
/// <c>WithAffiant</c> on an already-wired <see cref="ChatOptions"/>, or on a catalog shared between two
/// wiring sites). The check is a <em>top-level type test</em> over the tool list, and that bounds it
/// precisely: anything layered over an Affiant wrapper hides the marker. Two shapes do exactly that —
/// a host's own <see cref="DelegatingAIFunction"/> (telemetry, retry, redaction, argument coercion)
/// placed between <c>ChatOptions.Tools</c> and this wrapper, and <c>Affiant.AgentFramework</c>, which
/// rewrites <c>ChatOptions.Tools</c> with its own private wrapper type per agent run, after this
/// package's wire-up has already happened.
/// </para>
///
/// <para>
/// <b>What catches those.</b> <c>AffiantDelegatingAIFunction</c>'s invoke-time re-entrancy guard: an
/// ambient record of the onion in flight, so a nested wrapper refuses at the first invocation
/// whatever hides it, at any depth and across adapters. The rule — <b>exactly one Affiant adapter per
/// tool catalog / chat-client pipeline</b> — is therefore enforced, not merely documented; this
/// marker remains the earlier and friendlier of the two checks, because it fires before any turn can
/// run and leaves a refused wiring a pure no-op. See the package README's guard table.
/// </para>
/// </summary>
public interface IAffiantWrappedFunction
{
    /// <summary>The unwrapped function this wrapper delegates to.</summary>
    AIFunction AffiantInnerFunction { get; }
}
