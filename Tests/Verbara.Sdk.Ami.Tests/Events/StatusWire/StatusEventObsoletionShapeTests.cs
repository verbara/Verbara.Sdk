using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.Ami.Tests.Events.StatusWire;

/// <summary>
/// The shape of the obsoletion on the six <see cref="StatusEvent"/> members no supported version sends: one
/// diagnostic id, a message that says where the value is, and no promise of a removal. Read by reflection over the
/// member's name as a string, so this file compiles whether or not the members carry the attribute.
/// </summary>
public sealed partial class StatusEventObsoletionShapeTests
{
    public static TheoryData<string, string> ObsoletedMembers() => new()
    {
        { "State", "ChannelStateDesc" },
        { "CallerId", "CallerIdNum" },
        { "Account", "AccountCode" },
        { "BridgedChannel", "BridgeId" },
        { "Link", "BridgeId" },
        { "BridgedUniqueId", "BridgeId" },
    };

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex VersionNumber();

    private static ObsoleteAttribute? ObsoleteOf(string member) =>
        typeof(StatusEvent).GetProperty(member, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<ObsoleteAttribute>(inherit: false);

    [Theory]
    [MemberData(nameof(ObsoletedMembers))]
    public void Member_ShouldBeObsoleteUnderVsdk0002NamingItsReplacement_WhenNoSupportedVersionSendsIt(
        string member, string replacement)
    {
        typeof(StatusEvent).GetProperty(member, BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull($"{member} stays on StatusEvent; nothing is removed");

        var obsolete = ObsoleteOf(member);

        obsolete.Should().NotBeNull($"no supported Asterisk version sends {member}, so reading it is reported");
        using var scope = new AssertionScope();
        obsolete!.DiagnosticId.Should().Be("VSDK0002");
        obsolete.Message.Should().Contain(replacement, "the message names the member that carries the value");
        obsolete.Message.Should().NotContainEquivalentOf("remov", "an obsoletion says where the value is, never when the member goes");
        VersionNumber().IsMatch(obsolete.Message!).Should().BeFalse("the message names no release");
    }

    [Fact]
    public void Extension_ShouldNotBeObsolete_WhenItReadsTheExtenHeader()
    {
        typeof(StatusEvent).GetProperty("Extension", BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();

        ObsoleteOf("Extension").Should().BeNull("Extension is remapped to the Exten header, not obsoleted");
    }
}
