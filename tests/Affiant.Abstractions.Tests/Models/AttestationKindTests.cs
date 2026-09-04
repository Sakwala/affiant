namespace Affiant.Abstractions.Tests.Models;

using System.Reflection;
using Affiant.Abstractions.Models;
using Xunit;

/// <summary>
/// AZ-3, made structural: a human-verified session may attest <c>member</c>; a machine caller may
/// never. The rule is enforced by the types, not by a convention a reviewer has to notice.
/// </summary>
/// <remarks>
/// The reason this is a test and not only a code comment: "no code path can construct a member
/// attestation from a service principal" is a claim about the <em>whole</em> surface, and the only
/// way to keep it true as the surface grows is to assert it over the surface. A factory added later
/// that took a <see cref="Principal"/> and returned a <see cref="Attestor.Member"/> would compile
/// perfectly and fail here.
/// </remarks>
public class AttestationKindTests
{
    [Fact]
    public void NoAttestorKind_HasAPublicConstructor()
    {
        foreach (var kind in AttestorKinds())
        {
            var ctors = kind.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.True(
                ctors.Length == 0,
                $"AZ-3: {kind.Name} has a public constructor, so an attestation can be built without " +
                "going through the factory that decides which principal may make it.");
        }
    }

    /// <summary>
    /// The heart of it: nothing anywhere in the Abstractions surface turns a service principal into
    /// a member attestation.
    /// </summary>
    [Fact]
    public void NothingBuildsAMemberAttestation_FromAServicePrincipal()
    {
        var offenders = new List<string>();

        foreach (var type in typeof(Attestor).Assembly.GetTypes())
        {
            const BindingFlags All =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

            foreach (var method in type.GetMethods(All).Where(m => m.DeclaringType == type))
            {
                if (!Produces(method.ReturnType, typeof(Attestor.Member))) continue;
                if (!method.GetParameters().Any(p => Accepts(p.ParameterType, typeof(Principal.Service)))) continue;

                offenders.Add($"{type.FullName}.{method.Name}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "AZ-3: a machine caller may never attest member, and these members would let one:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    /// The member factory's signature is the enforcement: it takes a member principal, so there is
    /// no argument a caller could pass to reach it with a service one.
    /// </summary>
    [Fact]
    public void TheMemberFactory_TakesAMemberPrincipalAndNothingElse()
    {
        var factories = typeof(Attestor.Member)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Attestor.Member) && m.Name == nameof(Attestor.Member.Of))
            .ToArray();

        var factory = Assert.Single(factories);
        var parameter = Assert.Single(factory.GetParameters());
        Assert.Equal(typeof(Principal.Member), parameter.ParameterType);
    }

    [Fact]
    public void AMemberPrincipal_AttestsMember()
    {
        var attestor = Assert.IsType<Attestor.Member>(Attestor.For(new Principal.Member("ana")));
        Assert.Equal("member", attestor.Kind);
        Assert.Equal("ana", attestor.Id);
        Assert.Equal("ana", attestor.Subject);
    }

    [Fact]
    public void AServicePrincipalCarryingBothHalves_AttestsMemberViaRelay_AndNamesBoth()
    {
        var relay = new Principal.Service(
            "whatsapp-relay",
            new RelayAssertion("+94770000000", "wamid-1"),
            AssertedMember: "ana");

        var attestor = Assert.IsType<Attestor.MemberViaRelay>(Attestor.For(relay));
        Assert.Equal("member-via-relay", attestor.Kind);
        Assert.Equal("ana", attestor.MemberId);
        Assert.Equal("ana", attestor.Subject);
        Assert.Equal("whatsapp-relay", attestor.Relay.Principal);
        Assert.Equal("+94770000000", attestor.Relay.ChannelIdentity);
        Assert.Equal("wamid-1", attestor.Relay.MessageId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ana", null)]
    [InlineData(null, "wamid-1")]
    public void AServicePrincipalMissingEitherHalf_AttestsNothing(string? assertedMember, string? messageId)
    {
        var service = new Principal.Service(
            "whatsapp-relay",
            messageId is null ? null : new RelayAssertion("+94770000000", messageId),
            assertedMember);

        // A machine acting on its own behalf cannot agree to a write in a person's name, and half
        // an assertion is not an assertion.
        Assert.Null(Attestor.For(service));
        Assert.Null(Attestor.MemberViaRelay.Of(service));
    }

    /// <summary>
    /// A Standing Order is not reachable from a principal at all: nobody decided, so there is
    /// nobody to name — the policy and the version it fired under are the record.
    /// </summary>
    [Fact]
    public void AStandingOrderAttestation_IsNotReachableFromAPrincipal()
    {
        var offenders = typeof(Attestor.StandingOrder)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetParameters().Any(p => typeof(Principal).IsAssignableFrom(p.ParameterType)))
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(offenders);

        var order = Attestor.StandingOrder.Of("orders.auto-approve", "2026-09-01");
        Assert.Equal("standing-order", order.Kind);
        Assert.Equal("orders.auto-approve", order.PolicyId);
        Assert.Equal("2026-09-01", order.Version);
        Assert.Equal("orders.auto-approve", order.Subject);
    }

    [Fact]
    public void APolicyThatVersionsNothing_IsRecordedAsUnversioned_NotAsBlank()
    {
        Assert.Equal(
            Attestor.StandingOrder.Unversioned,
            Attestor.StandingOrder.Of("orders.auto-approve", version: null).Version);
        Assert.Equal(
            Attestor.StandingOrder.Unversioned,
            Attestor.StandingOrder.Of("orders.auto-approve", version: "").Version);
    }

    private static IEnumerable<Type> AttestorKinds() =>
    [
        typeof(Attestor.Member),
        typeof(Attestor.MemberViaRelay),
        typeof(Attestor.StandingOrder),
    ];

    private static bool Produces(Type returnType, Type produced) =>
        returnType == produced
        || (returnType.IsGenericType
            && returnType.GetGenericArguments().Any(a => a == produced));

    private static bool Accepts(Type parameterType, Type accepted) =>
        parameterType == accepted || parameterType.IsAssignableFrom(accepted);
}
