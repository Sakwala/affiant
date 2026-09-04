namespace Affiant.Abstractions;

/// <summary>
/// The protocol this build speaks, and nothing else.
///
/// <para>
/// <b>SR-4</b> — <i>every envelope carries the protocol version string it conforms to, and an
/// implementation states the version it targets.</i> The string is written once, here, and every
/// envelope that carries it reads it from this constant rather than repeating a literal, so a
/// version bump is one edit and cannot land half-applied.
/// </para>
///
/// <para>
/// The same string is stamped onto every <see cref="Models.DocketEntry"/> at filing, so a row read
/// years later says which version of the shapes it was written under rather than being interpreted
/// under whatever the reader happens to be running.
/// </para>
///
/// <para>
/// It is a version of the <b>protocol</b>, not of this package: the NuGet packages version
/// independently (see <c>Directory.Build.props</c>), and two packages a year apart may target the
/// same protocol. While the major is <c>0</c>, a schema-breaking change bumps the minor. A consumer
/// refuses a payload whose major differs from the one it targets and may warn on a newer minor.
/// </para>
/// </summary>
public static class AffiantProtocol
{
    /// <summary>
    /// The protocol version every envelope this build emits declares: <c>"0.1.0"</c>.
    ///
    /// The rulebook and the schemas that define it are
    /// <see href="https://github.com/Sakwala/affiant-protocol">Sakwala/affiant-protocol</see> at
    /// tag <c>v0.1.1</c>.
    /// </summary>
    public const string Version = "0.1.0";
}
