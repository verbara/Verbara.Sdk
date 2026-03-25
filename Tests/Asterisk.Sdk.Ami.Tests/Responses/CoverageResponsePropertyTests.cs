using Asterisk.Sdk.Ami.Responses;
using FluentAssertions;

namespace Asterisk.Sdk.Ami.Tests.Responses;

public sealed class CoverageResponsePropertyTests
{
    [Fact]
    public void MailboxCountResponse_ShouldHaveAllProperties()
    {
        var response = new MailboxCountResponse
        {
            Mailbox = "100@default",
            UrgMessages = 1,
            NewMessages = 3,
            OldMessages = 10
        };
        response.Mailbox.Should().Be("100@default");
        response.UrgMessages.Should().Be(1);
        response.NewMessages.Should().Be(3);
        response.OldMessages.Should().Be(10);
    }

    [Fact]
    public void MailboxStatusResponse_ShouldHaveProperties()
    {
        var response = new MailboxStatusResponse { Mailbox = "200@default", Waiting = true };
        response.Mailbox.Should().Be("200@default");
        response.Waiting.Should().BeTrue();
    }

    [Fact]
    public void SipShowPeerResponse_ShouldHaveKeyProperties()
    {
        var response = new SipShowPeerResponse
        {
            ChannelType = "SIP",
            ObjectName = "100",
            ChanObjectType = "peer",
            SecretExist = true,
            Md5SecretExist = false,
            RemoteSecretExist = false,
            Context = "default",
            Language = "en",
            AccountCode = "100",
            AmaFlags = "default",
            CallGroup = "1",
            PickupGroup = "1",
            TransferMode = "open",
            CallLimit = 5,
            BusyLevel = 1,
            Dynamic = true,
            CallerId = "\"User 100\" <100>",
            RegExpire = 120,
            SipAuthInsecure = false,
            SipNatSupport = true,
            Acl = true,
            SipT38support = false,
            SipDirectMedia = true,
            SipCanReinvite = true,
            SipVideoSupport = false,
            SipTextSupport = false,
            SipDtmfMode = "rfc2833",
            AddressIp = "192.168.1.100",
            AddressPort = 5060,
            DefaultAddrIp = "0.0.0.0",
            DefaultAddrPort = 5060,
            Status = "OK (15 ms)",
            SipUserAgent = "Yealink T46S",
            Codecs = "(alaw|ulaw)",
            CodecOrder = "alaw,ulaw",
            SipFromUser = "100",
            SipFromDomain = "pbx.local",
            CidCallingPres = "Presentation Allowed",
            VoiceMailbox = "100@default",
            LastMsgsSent = 0,
            MaxCallBr = 0,
            ParkingLot = "default",
            RegContact = "sip:100@192.168.1.100",
            QualifyFreq = 60,
            MaxForwards = 70,
            ToneZone = "us",
            SipUseReasonHeader = "yes",
            SipEncryption = "no",
            SipForcerport = "yes",
            SipRtpEngine = "asterisk",
            SipComedia = "no",
            SipSessTimers = "originate",
            SipSessRefresh = "uas",
            SipSessExpires = 1800,
            SipSessMin = 90,
            ToHost = "pbx.local",
            DefaultUsername = "100",
            RegExtension = "100",
            SipPromiscRedir = false,
            SipUserPhone = false,
            SipT38ec = "none",
            SipT38MaxDtgrm = 400,
            SipRtcpMux = "no",
            Description = "Test phone",
            Subscribecontext = "default",
            NamedCallgroup = "",
            NamedPickupgroup = "",
            Mohsuggest = "default"
        };
        response.ChannelType.Should().Be("SIP");
        response.ObjectName.Should().Be("100");
        response.Dynamic.Should().BeTrue();
        response.AddressIp.Should().Be("192.168.1.100");
        response.Status.Should().Be("OK (15 ms)");
        response.SipDtmfMode.Should().Be("rfc2833");
        response.SipUserAgent.Should().Be("Yealink T46S");
        response.CallLimit.Should().Be(5);
        response.RegExpire.Should().Be(120);
        response.SipT38MaxDtgrm.Should().Be(400);
        response.Description.Should().Be("Test phone");
    }

    [Fact]
    public void ExtensionStateResponse_ShouldHaveProperties()
    {
        var response = new ExtensionStateResponse
        {
            Exten = "100",
            Context = "default",
            Hint = "SIP/100",
            Status = 0,
            StatusText = "Idle"
        };
        response.Exten.Should().Be("100");
        response.Context.Should().Be("default");
        response.Hint.Should().Be("SIP/100");
        response.Status.Should().Be(0);
        response.StatusText.Should().Be("Idle");
    }

    [Fact]
    public void FaxLicenseStatusResponse_ShouldHaveProperties()
    {
        var response = new FaxLicenseStatusResponse { PortsLicensed = 4 };
        response.PortsLicensed.Should().Be(4);
    }

    [Fact]
    public void SkypeLicenseStatusResponse_ShouldHaveProperties()
    {
        var response = new SkypeLicenseStatusResponse { CallsLicensed = 10 };
        response.CallsLicensed.Should().Be(10);
    }
}
