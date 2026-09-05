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
    /// AZ-3 structurally: <b>every</b> public static member of this package that returns an attestor
    /// takes a principal, and the only one that returns a member attestor takes a
    /// <see cref="Principal.Member"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written over the whole public surface rather than over one named factory, because the defect
    /// this replaces was a second factory nobody had thought to name: <c>FromStorage(string)</c>
    /// minted a member attestation from a bare string, and the earlier reflection tests were blind
    /// to it — one looked only for a parameter accepting a service principal, the other filtered on
    /// the method name <c>Of</c>. A rule enforced against a list of names is a rule the next member
    /// added is exempt from.
    /// </para>
    /// <para>
    /// The rehydration factories still exist; they are <c>internal</c>, visible only to the packages
    /// named in <c>Affiant.Abstractions.csproj</c>, so a host cannot reach one.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryPublicFactoryReturningAnAttestor_TakesAPrincipalItIsEntitledTo()
    {
        var offenders = new List<string>();

        foreach (var type in typeof(Attestor).Assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.DeclaringType == type))
            {
                if (!Produces(member.ReturnType, typeof(Attestor))) continue;

                var parameters = member.GetParameters();

                // A member attestor is a person's signature: the only public way to one takes a
                // resolved member principal, and nothing else.
                if (Produces(member.ReturnType, typeof(Attestor.Member)))
                {
                    if (parameters.Length != 1 || parameters[0].ParameterType != typeof(Principal.Member))
                        offenders.Add($"{type.FullName}.{member.Name} -> {member.ReturnType.Name}");

                    continue;
                }

                // Every other public factory still starts from a principal, or from the policy that
                // fired — never from a bare identifier with nothing behind it.
                if (parameters.Length == 0)
                    offenders.Add($"{type.FullName}.{member.Name} -> {member.ReturnType.Name}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "AZ-3: every public factory that produces an attestor must start from a principal, and " +
            "only a member principal produces a member attestor. These do not:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    /// Rehydration is not public surface: reading an attestation back off a row is the stores'
    /// business, and a public factory taking a bare id is a way for a machine caller to name a
    /// person.
    /// </summary>
    [Fact]
    public void NoRehydrationFactoryIsPublic()
    {
        var exported = typeof(Attestor).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name == "FromStorage")
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToArray();

        Assert.True(
            exported.Length == 0,
            "AZ-3: rehydration is the stores' business, and these are public:\n" +
            string.Join("\n", exported));
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
