using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Attributes;
using FluentAssertions;
using FluentAssertions.Execution;

namespace Verbara.Sdk.Ami.Tests.Events;

/// <summary>
/// The 26 AMI event types that no supported Asterisk sends are gone from the generated registry: none of their
/// mapping names resolves to a typed class, the deserializer turns each into a base <see cref="ManagerEvent"/>, and
/// the registry holds exactly the mapped types that are not among them. <c>PeerEntry</c> stays typed, and carries no
/// <c>[Obsolete]</c>, because IAX still answers <c>IAXpeerlist</c> and <c>IAXpeers</c> with it.
/// </summary>
public sealed class RemovedEventMappingTests
{
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

    /// <summary>
    /// The expectation is derived from the Ami assembly at run time, so an event type added or removed for an unrelated
    /// reason moves both sides alike: only a removed name still mapped in the assembly can make it fail.
    /// </summary>
    [Fact]
    public void Registry_ShouldHoldTwentySixEntriesFewer_WhenTheRemovedTypesAreGone()
    {
        var mapped = MappedEventNames();
        var removed = new HashSet<string>(Names, StringComparer.OrdinalIgnoreCase);
        var kept = mapped.Count(name => !removed.Contains(name));

        using (new AssertionScope())
        {
            mapped.Should().HaveCount(GeneratedEventRegistry.Count,
                "the registry holds one entry per event mapping of the Ami assembly (the reading's control)");
            GeneratedEventRegistry.Count.Should().Be(kept,
                "the registry holds the mapped event types that are not among the 26 removed ones, and nothing else; "
                + "still mapped: {0}", string.Join(", ", mapped.Where(removed.Contains)));
        }
    }

    /// <summary>
    /// An event a supported Asterisk still sends carries no <c>[Obsolete]</c>. The type is looked up
    /// by name, so the test compiles without a suppression on both sides of the attribute's removal.
    /// </summary>
    [Fact]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test code: looks up a type of the Ami assembly by name, never trimmed here.")]
    public void PeerEntryEvent_ShouldCarryNoObsoleteAttribute_WhenIaxStillSendsIt()
    {
        var type = typeof(GeneratedEventRegistry).Assembly.GetType("Verbara.Sdk.Ami.Events.PeerEntryEvent", throwOnError: true)!;

        type.GetCustomAttribute<ObsoleteAttribute>(inherit: false).Should().BeNull(
            "IAX still answers IAXpeerlist and IAXpeers with PeerEntry, so its type is kept and promises no removal");
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

    /// <summary>The distinct mapping names of the Ami assembly's concrete event types, as the registry generator reads them.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test code: enumerates the Ami assembly's own types, never trimmed here.")]
    private static List<string> MappedEventNames() =>
    [
        .. typeof(GeneratedEventRegistry).Assembly.GetTypes()
            .Where(static type => type.IsClass && !type.IsAbstract && type.IsSubclassOf(typeof(ManagerEvent)))
            .Select(static type => type.GetCustomAttribute<VerbaraMappingAttribute>(inherit: false)?.Name)
            .OfType<string>()
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal),
    ];

    private static AmiMessage Message(string name) =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = name,
            ["Uniqueid"] = "1700000000.1",
            ["Channel"] = "PJSIP/a-1",
        });
}
