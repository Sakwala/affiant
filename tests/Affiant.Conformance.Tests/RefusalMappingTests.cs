using Affiant.Abstractions.Exceptions;
using Xunit;

using Affiant.Testing.ComplianceHarness.Conformance.Execution;

namespace Affiant.Conformance.Tests;

/// <summary>
/// A refusal the framework raises carries its own protocol code. The driver reads that code rather
/// than matching on the prose, so a refusal the framework declares can never reach a fixture as an
/// unhandled error — which reads in a run log as "the fixture could not run" and in a parity
/// manifest as "the rule is unimplemented", both of them false.
/// </summary>
public class RefusalMappingTests
{
    [Fact]
    public void ASubstanceRefusal_IsReadAsTheCodeItCarries()
    {
        var refusal = RefusalCodes.FromException(
            new AffiantSubstanceException("field \"status\" carries a value with Empty provenance"));

        Assert.NotNull(refusal);
        Assert.Equal(RefusalCodes.SubstanceRefused, refusal!.Code);
        Assert.Contains("Empty provenance", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APolicyRefusal_IsReadAsTheCodeItCarries()
    {
        var refusal = RefusalCodes.FromException(
            new AffiantPolicyException("the verdict carried a window that is not a review deadline"));

        Assert.NotNull(refusal);
        Assert.Equal(RefusalCodes.WireUpInvalid, refusal!.Code);
    }

    [Fact]
    public void AnExceptionTheFrameworkDoesNotDeclareARefusalFor_StillEscapes()
    {
        Assert.Null(RefusalCodes.FromException(new InvalidOperationException("the store is unavailable")));
    }
}
