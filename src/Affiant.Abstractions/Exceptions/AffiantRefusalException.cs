namespace Affiant.Abstractions.Exceptions;

/// <summary>
/// A refusal the gate raised rather than a failure it suffered: the proposal was <b>not</b> filed,
/// <b>not</b> broadcast, and no reviewer will ever see it.
///
/// <para>
/// Carries a <see cref="Code"/> from the protocol's refusal registry so a host can branch on it and
/// so the adapters can hand it back to the model as the error arm of a tool result rather than
/// letting a bare exception escape the tool seam. The registry's names are stable: an
/// implementation may add codes but never reuses these for another meaning.
/// </para>
/// </summary>
public class AffiantRefusalException : Exception
{
    /// <summary>The protocol refusal code for a proposal that swears to nothing (GT-3).</summary>
    public const string SubstanceRefusedCode = "substance-refused";

    /// <summary>The protocol refusal code for a wiring the gate cannot run (CV-1).</summary>
    public const string WireUpInvalidCode = "wireup-invalid";

    /// <summary>Creates a refusal carrying <paramref name="code"/>.</summary>
    public AffiantRefusalException(string code, string message) : base(message) => Code = code;

    /// <summary>Creates a refusal carrying <paramref name="code"/>, wrapping <paramref name="inner"/>.</summary>
    public AffiantRefusalException(string code, string message, Exception inner)
        : base(message, inner) => Code = code;

    /// <summary>The protocol refusal code. Never empty.</summary>
    public string Code { get; } = WireUpInvalidCode;
}

/// <summary>
/// A proposal that swears to nothing, refused at the gate before the policy chain runs (protocol
/// rule GT-3): every proposed field reads <c>Empty</c>, or a field asserts a value while its
/// provenance reads <c>Empty</c> — the hollow signature.
/// </summary>
public sealed class AffiantSubstanceException(string message)
    : AffiantRefusalException(SubstanceRefusedCode, message);

/// <summary>
/// An approval policy broke its own contract in a way no wire-up check could see, refused at
/// evaluation with nothing filed (protocol rule CV-1): a verdict carrying a window that is not a
/// review deadline, or an <c>EvaluateAsync</c> that threw.
/// </summary>
public sealed class AffiantPolicyException : AffiantRefusalException
{
    /// <summary>Creates a policy refusal carrying <c>wireup-invalid</c>.</summary>
    public AffiantPolicyException(string message) : base(WireUpInvalidCode, message) { }

    /// <summary>Creates a policy refusal carrying <c>wireup-invalid</c>, wrapping the policy's own throw.</summary>
    public AffiantPolicyException(string message, Exception inner)
        : base(WireUpInvalidCode, message, inner) { }
}
