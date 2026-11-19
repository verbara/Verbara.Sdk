using System.Reflection;
using FluentAssertions;
using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.Ami.Tests.Events.StatusWire;

/// <summary>
/// The six <see cref="StatusEvent"/> members no supported version sends a header for, marked in 2.9.0, are gone from
/// 2.11.0 on; the members that carry the value stay. Read by reflection over the member's name as a string, so this
/// file compiles with and without the removed members.
/// </summary>
public sealed class StatusEventRemovedMembersTests
{
    public static TheoryData<string, string> RemovedMembers() => new()
    {
        { "State", "ChannelStateDesc" },
        { "CallerId", "CallerIdNum" },
        { "Account", "AccountCode" },
        { "BridgedChannel", "BridgeId" },
        { "Link", "BridgeId" },
        { "BridgedUniqueId", "BridgeId" },
    };

    private static PropertyInfo? PropertyOf(string member) =>
        typeof(StatusEvent).GetProperty(member, BindingFlags.Public | BindingFlags.Instance);

    [Theory]
    [MemberData(nameof(RemovedMembers))]
    public void Member_ShouldBeGoneWhileItsReplacementStays_WhenNoSupportedVersionSendsIt(string member, string replacement)
    {
        PropertyOf(replacement).Should().NotBeNull($"{replacement} carries the value and stays on StatusEvent");
        PropertyOf(member).Should().BeNull($"{member} was marked VSDK0002 in 2.9.0 and removed in 2.11.0");
    }

    [Fact]
    public void Extension_ShouldNotBeObsolete_WhenItReadsTheExtenHeader()
    {
        PropertyOf("Extension").Should().NotBeNull();

        PropertyOf("Extension")!.GetCustomAttribute<ObsoleteAttribute>(inherit: false)
            .Should().BeNull("Extension is remapped to the Exten header, not obsoleted");
    }
}
