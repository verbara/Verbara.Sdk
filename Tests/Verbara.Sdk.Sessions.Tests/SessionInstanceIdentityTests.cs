using System.Xml.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Tests.Shared.Sessions;

namespace Verbara.Sdk.Sessions.Tests;

/// <summary>
/// The public instance identity a session notification carries, as a consumer sees it without a session engine: a value
/// type compared by value that always names the instance; a member a notification built with the positional constructor leaves null, which is part of
/// the notification's value; and its published documentation, which says that null means unknown and that
/// <see cref="SessionDomainEvent.ServerId"/> is unchanged.
/// </summary>
/// <remarks>
/// The member and the type are named only through <see cref="InstanceIdentityNames"/>.
/// </remarks>
public sealed class SessionInstanceIdentityTests
{
    private static readonly DateTimeOffset Ts = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Identity_ShouldBeEqualByValue_WhenTheSystemNamesAreEqual()
    {
        var first = InstanceIdentityNames.NewIdentity("pbx-one");
        var same = InstanceIdentityNames.NewIdentity("pbx-one");

        using (new AssertionScope())
        {
            first.Should().NotBeSameAs(same, "premise: two instances built apart");
            first.Should().Be(same, "the identity is a value: equal system names, equal identity");
            first.GetHashCode().Should().Be(same.GetHashCode(), "equal values hash alike");
        }
    }

    [Theory]
    [InlineData("pbx-two")]
    [InlineData("PBX-ONE")]
    public void Identity_ShouldNotBeEqual_WhenTheSystemNamesDiffer(string systemName)
    {
        var first = InstanceIdentityNames.NewIdentity("pbx-one");
        var other = InstanceIdentityNames.NewIdentity(systemName);

        other.Should().NotBe(first,
            "an identity with another system name is another identity, compared as Asterisk wrote it (ordinal)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Identity_ShouldRefuseTheValue_WhenTheSystemNameIsMissing(string? systemName)
    {
        // An instance that gives no system name has no identity: the notification's member is null (unknown), never an
        // identity naming nothing.
        var build = () => InstanceIdentityNames.NewIdentity(systemName!);

        build.Should().Throw<ArgumentException>("an identity always names its instance; null on the notification means unknown");
    }

    [Fact]
    public void PositionalConstructor_ShouldNameNoInstance_WhenAConsumerBuildsANotification()
    {
        var built = new CallStartedEvent("s1", "srv1", Ts, CallDirection.Inbound, "1001");

        using (new AssertionScope())
        {
            InstanceIdentityNames.IdentityOf(built).Should().BeNull(
                "a notification built with the positional constructor alone names no instance");
            built.ServerId.Should().Be("srv1", "the server id is the one the consumer gave");
        }
    }

    [Fact]
    public void Equals_ShouldIncludeTheIdentity_WhenTwoNotificationsDifferOnlyByIt()
    {
        var built = new CallStartedEvent("s1", "srv1", Ts, CallDirection.Inbound, "1001");
        var named = InstanceIdentityNames.WithSystemName(built, "pbx-one");
        var namedAgain = InstanceIdentityNames.WithSystemName(built, "pbx-one");
        var other = InstanceIdentityNames.WithSystemName(built, "pbx-two");

        using (new AssertionScope())
        {
            named.Should().BeOfType<CallStartedEvent>("premise: the copy keeps the notification's kind");
            named.ServerId.Should().Be(built.ServerId, "naming an instance leaves the server id unchanged");
            built.Should().NotBe(named, "a notification that names no instance is not equal to one that names a known one");
            named.Should().Be(namedAgain, "the identity is part of the notification's value");
            named.GetHashCode().Should().Be(namedAgain.GetHashCode());
            named.Should().NotBe(other, "two notifications that differ only by their instance are not equal");
            named.ToString().Should().Contain(InstanceIdentityNames.MemberName + " = ",
                "the printed value includes the identity");
        }
    }

    [Fact]
    public void PublishedDocumentation_ShouldSayNullMeansUnknownAndServerIdIsUnchanged_WhenAConsumerReadsIt()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Verbara.Sdk.Sessions.xml");
        File.Exists(file).Should().BeTrue($"premise: the build emits the package's documentation file beside the assembly ({file})");
        var member = XDocument.Load(file).Descendants("member")
            .FirstOrDefault(m => (string?)m.Attribute("name") == InstanceIdentityNames.DocumentationId);
        var text = member is null
            ? ""
            : string.Join(" ", member.DescendantNodes().Select(n => n switch
            {
                XText t => t.Value,
                XElement { Name.LocalName: "see" } e => ((string?)e.Attribute("cref") ?? (string?)e.Attribute("langword") ?? ""),
                _ => "",
            }));
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        new
        {
            Documented = member is not null,
            SaysNullIsUnknown = normalized.Contains("null", StringComparison.Ordinal)
                && normalized.Contains("unknown", StringComparison.OrdinalIgnoreCase),
            NamesServerId = normalized.Contains("ServerId", StringComparison.Ordinal),
        }.Should().BeEquivalentTo(
            new { Documented = true, SaysNullIsUnknown = true, NamesServerId = true },
            $"the published documentation of {InstanceIdentityNames.DocumentationId} says that null means unknown and "
            + $"that ServerId is unchanged. Published: \"{normalized}\"");
    }
}
