using Asterisk.NetAot.Agi.Server;
using FluentAssertions;

namespace Asterisk.NetAot.Agi.Tests.Server;

public class AgiRequestTests
{
    [Fact]
    public void Parse_ShouldExtractAllFields()
    {
        var lines = new[]
        {
            "agi_network: yes",
            "agi_network_script: GetCallerRecord",
            "agi_request: agi://192.168.0.2/GetCallerRecord",
            "agi_channel: PJSIP/2000-00000001",
            "agi_language: en",
            "agi_uniqueid: 1234567890.0",
            "agi_callerid: 5551234567",
            "agi_calleridname: John Doe",
            "agi_context: from-internal",
            "agi_extension: 1234",
            "agi_priority: 1"
        };

        var request = AgiRequest.Parse(lines);

        request.IsNetwork.Should().BeTrue();
        request.Script.Should().Be("GetCallerRecord");
        request.Channel.Should().Be("PJSIP/2000-00000001");
        request.Language.Should().Be("en");
        request.UniqueId.Should().Be("1234567890.0");
        request.CallerId.Should().Be("5551234567");
        request.CallerIdName.Should().Be("John Doe");
        request.Context.Should().Be("from-internal");
        request.Extension.Should().Be("1234");
        request.Priority.Should().Be(1);
    }

    [Fact]
    public void Parse_NonNetworkRequest_ShouldUseFallbackScript()
    {
        var lines = new[]
        {
            "agi_request: /var/lib/asterisk/agi-bin/test.agi",
            "agi_channel: SIP/2000",
            "agi_uniqueid: 999.1"
        };

        var request = AgiRequest.Parse(lines);

        request.IsNetwork.Should().BeFalse();
        request.Script.Should().Be("/var/lib/asterisk/agi-bin/test.agi");
    }
}
