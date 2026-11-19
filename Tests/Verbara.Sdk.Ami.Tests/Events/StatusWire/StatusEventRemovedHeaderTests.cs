using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;

namespace Verbara.Sdk.Ami.Tests.Events.StatusWire;

/// <summary>
/// A generator test, not wire evidence: no supported Asterisk version sends a <c>State</c> header on a Status frame.
/// The message below is built by hand to show that a header whose member 2.11.0 removed is still kept, in
/// <see cref="ManagerEvent.RawFields"/>, so a server that did send it would still be read.
/// </summary>
public sealed class StatusEventRemovedHeaderTests
{
    [Fact]
    public void Deserialize_ShouldKeepTheStateHeaderInRawFields_WhenAHandBuiltMessageCarriesIt()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = "Status",
            ["Channel"] = "PJSIP/100-00000001",
            ["State"] = "Up",
            ["ChannelStateDesc"] = "Ring",
        };

        var status = GeneratedEventDeserializer.Deserialize(new AmiMessage(fields))
            .Should().BeOfType<StatusEvent>().Subject;

        using var scope = new AssertionScope();
        status.RawFields.Should().Contain("State", "Up", "a header with no member is kept as it arrived");
        status.ChannelStateDesc.Should().Be("Ring", "the State header is not bound to the member that carries the state");
    }
}
