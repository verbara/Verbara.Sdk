using FluentAssertions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;

namespace Verbara.Sdk.Ami.Tests.Events.StatusWire;

/// <summary>
/// A generator test, not wire evidence: no supported Asterisk version sends a <c>State</c> header on a Status
/// frame. The message below is built by hand to show that the generated deserializer still binds an obsolete
/// member, so a server that did send the header would still be read.
/// </summary>
public sealed class StatusEventObsoleteBindingTests
{
    [Fact]
    public void Deserialize_ShouldStillBindTheObsoleteState_WhenAHandBuiltMessageCarriesIt()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Event"] = "Status",
            ["Channel"] = "PJSIP/100-00000001",
            ["State"] = "Up",
        };

        var status = GeneratedEventDeserializer.Deserialize(new AmiMessage(fields))
            .Should().BeOfType<StatusEvent>().Subject;

        // Reading the member is reported under VSDK0002, so the read sits inside the pragma.
#pragma warning disable VSDK0002
        status.State.Should().Be("Up", "the generator keeps binding a member it marks obsolete");
#pragma warning restore VSDK0002
    }
}
