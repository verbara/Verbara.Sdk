using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;

namespace Verbara.Sdk.Ami.Tests.Deprecation;

/// <summary>
/// The seven AMI types whose wire name no supported Asterisk registers or emits, marked in 2.10.0, are gone from 2.11.0
/// on, and the types around them that answer a live name stay unmarked. The removed types are looked up by name, so
/// this file compiles with and without them.
/// </summary>
public sealed class UnregisteredWireNameRemovalTests
{
    public static TheoryData<string> RemovedTypeNames() => new()
    {
        "Verbara.Sdk.Ami.Actions.DbPrefixGetAction",
        "Verbara.Sdk.Ami.Actions.ExecAction",
        "Verbara.Sdk.Ami.Events.JitterBufStatsEvent",
        "Verbara.Sdk.Ami.Events.MasqueradeEvent",
        "Verbara.Sdk.Ami.Events.PriEventEvent",
        "Verbara.Sdk.Ami.Events.RtpReceiverStatEvent",
        "Verbara.Sdk.Ami.Events.RtpSenderStatEvent",
    };

    [Theory]
    [MemberData(nameof(RemovedTypeNames))]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test code: looks up a type of the Ami assembly by name, never trimmed here.")]
    public void Type_ShouldBeAbsentFromTheAmiAssembly_WhenNoSupportedAsteriskKnowsItsWireName(string typeName)
    {
        // The control: a type of the same assembly and namespace that is kept resolves by the same lookup.
        typeof(StatusEvent).Assembly.GetType("Verbara.Sdk.Ami.Events.StatusEvent").Should().NotBeNull(
            "the lookup by name finds a kept type, so a null below means the type is gone");

        typeof(StatusEvent).Assembly.GetType(typeName).Should().BeNull(
            $"{typeName} was marked VSDK0007 in 2.10.0 and removed in 2.11.0, as its warning announced");
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
            .Should().BeNull("the clause covered exactly seven types by name, and these answer a live name or are out of scope");
    }
}
