using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.SourceGenerators;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The event deserializer reads every member a class inherits, and <c>UniqueId</c>, under the prefix its
/// <c>InheritedHeaderPrefix</c> attribute names, after the unprefixed read of the layer above, and only for an event
/// that derives from that class. Run over a synthetic compilation (<see cref="GeneratorHarness"/>).
/// </summary>
public sealed class InheritedHeaderPrefixEmissionTests
{
    private const string Source = """
        using System;
        using Verbara.Sdk;
        using Verbara.Sdk.Attributes;

        namespace Verbara.Sdk.Ami.Events.Base
        {
            [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
            internal sealed class InheritedHeaderPrefixAttribute(string prefix) : Attribute
            {
                public string Prefix { get; } = prefix;
            }

            public class ChannelProbeBase : ManagerEvent
            {
                public string? Channel { get; set; }
                public int? Priority { get; set; }
            }

            [InheritedHeaderPrefix("Parkee")]
            public abstract class ParkingProbeBase : ChannelProbeBase
            {
                public string? ParkingSpace { get; set; }
            }
        }

        namespace Probe
        {
            using Verbara.Sdk.Ami.Events.Base;

            [VerbaraMapping("ProbeParked")]
            public sealed class ProbeParkedEvent : ParkingProbeBase
            {
                public string? Note { get; set; }
            }

            [VerbaraMapping("ProbeChannel")]
            public sealed class ProbeChannelEvent : ChannelProbeBase
            {
                public string? Note { get; set; }
            }
        }
        """;

    [Fact]
    public void Deserializer_ShouldReadTheInheritedMembersUnderThePrefix_OnlyForAnEventDerivingFromThePrefixedClass()
    {
        var run = GeneratorHarness.Run(Source, new EventRegistryGenerator(), new EventDeserializerGenerator());
        var text = run.Single(nameof(EventDeserializerGenerator)).Text;

        var baseBlock = text.IndexOf("if (evt is global::Verbara.Sdk.Ami.Events.Base.ChannelProbeBase channelProbeBase)", StringComparison.Ordinal);
        var prefixBlock = text.IndexOf("if (evt is global::Verbara.Sdk.Ami.Events.Base.ParkingProbeBase parkingProbeBasePrefixed)", StringComparison.Ordinal);

        using var scope = new AssertionScope();
        run.WarningsAndErrors.Should().BeEmpty();
        baseBlock.Should().BeGreaterThan(-1);
        prefixBlock.Should().BeGreaterThan(baseBlock, "the prefixed read follows the unprefixed one, so it wins");
        text.Should().Contain("parkingProbeBasePrefixed.UniqueId = msg[\"ParkeeUniqueid\"];");
        text.Should().Contain("parkingProbeBasePrefixed.Channel = msg[\"ParkeeChannel\"];");
        text.Should().Contain("int.TryParse(msg[\"ParkeePriority\"]");
        text.Should().Contain("parkingProbeBase.ParkingSpace = msg[\"ParkingSpace\"];", "the prefixed class's own members are read unprefixed");
        text.Should().NotContain("msg[\"ParkeeParkingSpace\"]", "only inherited members take the prefix");
        text.Should().NotContain("msg[\"ParkeeNote\"]", "a leaf member is read unprefixed");
        text.Split("Parkee").Length.Should().Be(1 + 3, "three reads take the prefix: UniqueId, Channel and Priority");
    }

    [Fact]
    public void Deserializer_ShouldEmitNoPrefixedRead_WhenNoClassCarriesThePrefixAttribute()
    {
        var run = GeneratorHarness.Run(Source.Replace("[InheritedHeaderPrefix(\"Parkee\")]", "", StringComparison.Ordinal),
            new EventRegistryGenerator(), new EventDeserializerGenerator());
        var text = run.Single(nameof(EventDeserializerGenerator)).Text;

        using var scope = new AssertionScope();
        run.WarningsAndErrors.Should().BeEmpty();
        text.Should().NotContain("Parkee");
        text.Should().NotContain("Prefixed");
    }
}
