using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.Ami.Tests.Deprecation;

/// <summary>
/// The shape of the obsoletion on the seven AMI types whose wire name no supported Asterisk registers or emits: one
/// diagnostic id, the link to its heading in the 2.10.0 migration guide, a warning rather than an error, and a message
/// that names what to use where something exists and the one release that removes the type. The attribute is read by
/// reflection, and the <c>typeof</c> sites sit inside a scoped pragma, so this file compiles whether or not the types
/// carry the mark.
/// </summary>
public sealed partial class UnregisteredWireNameObsoletionShapeTests
{
    private const string Link = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/migration-2.10.0.md#vsdk0007";

    /// <summary>Type, the wire name its message names, and what the message names to use instead (empty when nothing).</summary>
    public static TheoryData<Type, string, string> MarkedTypes() => new()
    {
#pragma warning disable VSDK0007 // the marked types are the subject
        { typeof(DbPrefixGetAction), "DBPrefixGet", "DbGetTreeAction" },
        { typeof(ExecAction), "exec", "CommandAction" },
        { typeof(JitterBufStatsEvent), "JitterBufStats", "" },
        { typeof(MasqueradeEvent), "Masquerade", "" },
        { typeof(PriEventEvent), "PRIEvent", "" },
        { typeof(RtpReceiverStatEvent), "RTPReceiverStat", "" },
        { typeof(RtpSenderStatEvent), "RTPSenderStat", "" },
#pragma warning restore VSDK0007
    };

    [GeneratedRegex(@"Verbara\.Sdk \d+\.\d+\.\d+")]
    private static partial Regex SdkRelease();

    [Theory]
    [MemberData(nameof(MarkedTypes))]
    public void Type_ShouldBeObsoleteUnderVsdk0007NamingTheRemovalRelease_WhenNoSupportedAsteriskKnowsItsWireName(
        Type type, string wireName, string replacement)
    {
        var obsolete = type.GetCustomAttribute<ObsoleteAttribute>(inherit: false);

        obsolete.Should().NotBeNull($"no supported Asterisk registers or emits {wireName}, so naming the type is reported");
        using var scope = new AssertionScope();
        obsolete!.DiagnosticId.Should().Be("VSDK0007");
        obsolete.UrlFormat.Should().Be(Link, "the warning's help link opens the id's heading of the 2.10.0 migration guide");
        obsolete.IsError.Should().BeFalse("a mark is a warning for one release, not a removal");
        obsolete.Message.Should().Contain(wireName, "the message names the wire name nothing answers");
        if (replacement.Length > 0)
            obsolete.Message.Should().Contain(replacement, "the message names what to use instead");
        obsolete.Message.Should().EndWith("Removed in Verbara.Sdk 2.11.0.", "the message names the release that removes the type");
        SdkRelease().Matches(obsolete.Message!).Select(static m => m.Value).Should().Equal(["Verbara.Sdk 2.11.0"],
            "the message names that one SDK release and no other");
    }

    public static TheoryData<Type> UnmarkedTypes() => new()
    {
        typeof(SendFlashAction), typeof(DbGetTreeAction), typeof(CommandAction), typeof(RtcpReceivedEvent), typeof(RtcpSentEvent),
    };

    [Theory]
    [MemberData(nameof(UnmarkedTypes))]
    public void Type_ShouldNotBeObsolete_WhenItIsOutsideTheClosedSet(Type type)
    {
        type.GetCustomAttribute<ObsoleteAttribute>(inherit: false)
            .Should().BeNull("the clause covers exactly seven types by name, and these answer a live name or are out of scope");
    }
}
