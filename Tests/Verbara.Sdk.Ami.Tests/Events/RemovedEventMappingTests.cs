using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Events;

/// <summary>
/// The 26 AMI event types that no supported Asterisk sends are gone from the generated registry: none of their
/// mapping names resolves to a typed class, the deserializer turns each into a base <see cref="ManagerEvent"/>, and
/// the registry holds exactly 26 entries fewer. <c>PeerEntry</c> stays typed, because IAX still answers
/// <c>IAXpeerlist</c> and <c>IAXpeers</c> with it.
/// </summary>
public sealed class RemovedEventMappingTests
{
    /// <summary>The registry's size with the 26 types, measured before their removal.</summary>
    private const int CountBeforeRemoval = 269;

    /// <summary>The mapping names of the 26 removed types, as their <c>[VerbaraMapping]</c> spelled them.</summary>
    private static readonly string[] Names =
    [
        "Bridge", "Dial", "Join", "Leave", "Link", "Unlink", "Paused", "Unpaused",
        "MeetMeEnd", "MeetMeJoin", "MeetMeLeave", "MeetMeMute", "MeetMeStopTalking", "MeetMeTalking", "MeetMeTalkingRequest",
        "MonitorStart", "MonitorStop",
        "SkypeAccountStatus", "SkypeBuddyEntry", "SkypeBuddyListComplete", "SkypeBuddyStatus", "SkypeChatMessage",
        "SkypeLicense", "SkypeLicenseListComplete",
        "ZapShowChannels", "ZapShowChannelsComplete",
    ];

    public static TheoryData<string> RemovedNames => new(Names);

    [Fact]
    public void RemovedNames_ShouldNameTwentySixDistinctTypes_WhenListed()
    {
        // The list's own control: a name dropped or repeated would shrink what the other cases measure.
        Names.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(26);
    }

    [Theory]
    [MemberData(nameof(RemovedNames))]
    public void Registry_ShouldResolveNoTypedClass_WhenGivenARemovedMappingName(string name)
    {
        var created = GeneratedEventRegistry.Create(name);
        var deserialized = GeneratedEventDeserializer.Deserialize(Message(name));

        using (new AssertionScope())
        {
            created.Should().BeNull($"no type maps '{name}' once its obsolete type is removed");
            deserialized.GetType().Should().Be<ManagerEvent>( $"'{name}' arrives as a base ManagerEvent");
            deserialized.EventType.Should().Be(name, "the name is kept as it arrived");
            deserialized.RawFields.Should().ContainKey("Channel", "every header is kept");
        }
    }

    [Fact]
    public void Registry_ShouldHoldTwentySixEntriesFewer_WhenTheRemovedTypesAreGone()
    {
        GeneratedEventRegistry.Count.Should().Be(CountBeforeRemoval - 26,
            "the registry loses exactly the 26 removed types and nothing else");
    }

    [Theory]
    [InlineData("PeerEntry", "Verbara.Sdk.Ami.Events.PeerEntryEvent")]
    [InlineData("ConfbridgeJoin", "Verbara.Sdk.Ami.Events.ConfbridgeJoinEvent")]
    public void Registry_ShouldStillResolveTheTypedClass_WhenGivenAKeptMappingName(string name, string typeName)
    {
        // Compared by name: PeerEntryEvent carries [Obsolete] until it loses it in the same change, and this control
        // must compile on both sides of that edit without a suppression.
        var created = GeneratedEventRegistry.Create(name);
        created.Should().NotBeNull($"'{name}' is registered");
        created!.GetType().FullName.Should().Be(typeName);
        GeneratedEventDeserializer.Deserialize(Message(name)).GetType().FullName.Should().Be(typeName,
            $"'{name}' is still sent by a supported Asterisk and keeps its type");
    }

    private static AmiMessage Message(string name) =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = name,
            ["Uniqueid"] = "1700000000.1",
            ["Channel"] = "PJSIP/a-1",
        });
}
