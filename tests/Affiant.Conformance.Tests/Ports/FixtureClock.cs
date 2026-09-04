namespace Affiant.Conformance.Tests.Ports;

/// <summary>
/// The fixture's clock, as the framework's own <see cref="TimeProvider"/> seam.
/// </summary>
/// <remarks>
/// It moves only when a step's <c>at</c> moves it (<c>RUNNER.md</c> §5.6), so every fixture is
/// deterministic and no instant the framework stamps comes from the wall clock.
/// </remarks>
internal sealed class FixtureClock : TimeProvider
{
    /// <summary>The instant the fixture has reached.</summary>
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => Now;

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
