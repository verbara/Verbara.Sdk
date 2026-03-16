using DashboardExample.Models;
using FluentAssertions;

namespace DashboardExample.Tests.Models;

public class ExtensionConfigTests
{
    [Fact]
    public void ToPjsipEndpoint_ShouldContainRequiredFields()
    {
        var config = CreateDefaultConfig();
        var endpoint = config.ToPjsipEndpoint();

        endpoint["type"].Should().Be("endpoint");
        endpoint["context"].Should().Be("from-internal");
        endpoint["disallow"].Should().Be("all");
        endpoint["allow"].Should().Be("ulaw,alaw");
        endpoint["aors"].Should().Be("1001-aor");
        endpoint["auth"].Should().Be("1001-auth");
        endpoint["callerid"].Should().Be("\"Test User\" <1001>");
        endpoint["direct_media"].Should().Be("no");
    }

    [Fact]
    public void ToPjsipEndpoint_ShouldNotContainOutboundAuth()
    {
        var config = CreateDefaultConfig();
        var endpoint = config.ToPjsipEndpoint();

        endpoint.Should().NotContainKey("outbound_auth");
    }

    [Fact]
    public void ToPjsipEndpoint_ShouldIncludeCallGroup_WhenSet()
    {
        var config = CreateDefaultConfig();
        config.CallGroup = "1";
        config.PickupGroup = "1";

        var endpoint = config.ToPjsipEndpoint();

        endpoint["call_group"].Should().Be("1");
        endpoint["pickup_group"].Should().Be("1");
    }

    [Fact]
    public void ToPjsipEndpoint_ShouldNotIncludeCallGroup_WhenEmpty()
    {
        var config = CreateDefaultConfig();
        config.CallGroup = null;
        config.PickupGroup = null;

        var endpoint = config.ToPjsipEndpoint();

        endpoint.Should().NotContainKey("call_group");
        endpoint.Should().NotContainKey("pickup_group");
    }

    [Fact]
    public void ToPjsipAuth_ShouldContainCredentials()
    {
        var config = CreateDefaultConfig();
        var auth = config.ToPjsipAuth();

        auth["type"].Should().Be("auth");
        auth["auth_type"].Should().Be("userpass");
        auth["username"].Should().Be("1001");
        auth["password"].Should().Be("secret123");
    }

    [Fact]
    public void ToPjsipAor_ShouldSetMaxContacts_AndNoStaticContact()
    {
        var config = CreateDefaultConfig();
        var aor = config.ToPjsipAor();

        aor["type"].Should().Be("aor");
        aor["max_contacts"].Should().Be("1");
        aor["qualify_frequency"].Should().Be("60");
        aor["remove_existing"].Should().Be("yes");
        aor.Should().NotContainKey("contact");
    }

    [Fact]
    public void ToSipPeer_ShouldContainRequiredFields()
    {
        var config = CreateDefaultConfig();
        config.Technology = ExtensionTechnology.Sip;

        var peer = config.ToSipPeer();

        peer["type"].Should().Be("friend");
        peer["host"].Should().Be("dynamic");
        peer["secret"].Should().Be("secret123");
        peer["context"].Should().Be("from-internal");
        peer["callerid"].Should().Be("\"Test User\" <1001>");
    }

    [Fact]
    public void ToIaxPeer_ShouldContainRequiredFields()
    {
        var config = CreateDefaultConfig();
        config.Technology = ExtensionTechnology.Iax2;

        var peer = config.ToIaxPeer();

        peer["type"].Should().Be("friend");
        peer["host"].Should().Be("dynamic");
        peer["secret"].Should().Be("secret123");
        peer["context"].Should().Be("from-internal");
        peer.Should().NotContainKey("trunk");
    }

    [Fact]
    public void FromPjsipSections_ShouldParseCorrectly()
    {
        var endpoint = new Dictionary<string, string>
        {
            ["type"] = "endpoint",
            ["context"] = "from-internal",
            ["allow"] = "ulaw,alaw",
            ["dtmf_mode"] = "rfc4733",
            ["transport"] = "transport-udp",
            ["force_rport"] = "yes",
            ["direct_media"] = "yes",
            ["callerid"] = "\"Test User\" <1001>",
            ["call_group"] = "1",
            ["pickup_group"] = "2",
        };
        var auth = new Dictionary<string, string>
        {
            ["type"] = "auth",
            ["auth_type"] = "userpass",
            ["username"] = "1001",
            ["password"] = "secret123",
        };
        var aor = new Dictionary<string, string>
        {
            ["type"] = "aor",
            ["max_contacts"] = "1",
            ["qualify_frequency"] = "60",
            ["remove_existing"] = "yes",
        };

        var config = ExtensionConfig.FromPjsipSections("1001", endpoint, auth, aor);

        config.Extension.Should().Be("1001");
        config.Technology.Should().Be(ExtensionTechnology.PjSip);
        config.Name.Should().Be("Test User");
        config.Password.Should().Be("secret123");
        config.Context.Should().Be("from-internal");
        config.DirectMedia.Should().BeTrue();
        config.CallGroup.Should().Be("1");
        config.PickupGroup.Should().Be("2");
        config.Transport.Should().Be("udp");
        config.ForceRport.Should().BeTrue();
    }

    [Fact]
    public void RoundTrip_PjsipSections()
    {
        var original = CreateDefaultConfig();
        original.CallGroup = "1";
        original.PickupGroup = "2";

        var endpoint = original.ToPjsipEndpoint();
        var auth = original.ToPjsipAuth();
        var aor = original.ToPjsipAor();

        var restored = ExtensionConfig.FromPjsipSections("1001", endpoint, auth, aor);

        restored.Extension.Should().Be("1001");
        restored.Technology.Should().Be(ExtensionTechnology.PjSip);
        restored.Name.Should().Be("Test User");
        restored.Password.Should().Be("secret123");
        restored.Context.Should().Be("from-internal");
        restored.Codecs.Should().Be("ulaw,alaw");
        restored.CallGroup.Should().Be("1");
        restored.PickupGroup.Should().Be("2");
        restored.DirectMedia.Should().BeFalse();
    }

    [Fact]
    public void FromSipPeer_ShouldParseCorrectly()
    {
        var section = new Dictionary<string, string>
        {
            ["type"] = "friend",
            ["host"] = "dynamic",
            ["secret"] = "secret123",
            ["context"] = "from-internal",
            ["allow"] = "ulaw,alaw",
            ["dtmfmode"] = "rfc4733",
            ["callerid"] = "\"Test User\" <1001>",
            ["call-group"] = "1",
            ["pickup-group"] = "2",
            ["nat"] = "force_rport,comedia",
            ["directmedia"] = "no",
        };

        var config = ExtensionConfig.FromSipPeer("1001", section);

        config.Extension.Should().Be("1001");
        config.Technology.Should().Be(ExtensionTechnology.Sip);
        config.Name.Should().Be("Test User");
        config.Password.Should().Be("secret123");
        config.Context.Should().Be("from-internal");
        config.CallGroup.Should().Be("1");
        config.PickupGroup.Should().Be("2");
        config.ForceRport.Should().BeTrue();
        config.DirectMedia.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_SipPeer()
    {
        var original = CreateDefaultConfig();
        original.Technology = ExtensionTechnology.Sip;
        original.CallGroup = "1";
        original.PickupGroup = "2";

        var section = original.ToSipPeer();
        var restored = ExtensionConfig.FromSipPeer("1001", section);

        restored.Extension.Should().Be("1001");
        restored.Technology.Should().Be(ExtensionTechnology.Sip);
        restored.Name.Should().Be("Test User");
        restored.Password.Should().Be("secret123");
        restored.Context.Should().Be("from-internal");
        restored.Codecs.Should().Be("ulaw,alaw");
        restored.DirectMedia.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_IaxPeer()
    {
        var original = CreateDefaultConfig();
        original.Technology = ExtensionTechnology.Iax2;

        var section = original.ToIaxPeer();
        var restored = ExtensionConfig.FromIaxPeer("1001", section);

        restored.Extension.Should().Be("1001");
        restored.Technology.Should().Be(ExtensionTechnology.Iax2);
        restored.Password.Should().Be("secret123");
        restored.Context.Should().Be("from-internal");
        restored.Codecs.Should().Be("ulaw,alaw");
    }

    [Fact]
    public void Codecs_ShouldRoundTrip()
    {
        var config = CreateDefaultConfig();
        config.Codecs = "g729,opus,g722";

        var endpoint = config.ToPjsipEndpoint();
        endpoint["allow"].Should().Be("g729,opus,g722");

        var restored = ExtensionConfig.FromPjsipSections("1001", endpoint, null, null);
        restored.Codecs.Should().Be("g729,opus,g722");
    }

    private static ExtensionConfig CreateDefaultConfig() => new()
    {
        Extension = "1001",
        Name = "Test User",
        Technology = ExtensionTechnology.PjSip,
        Password = "secret123",
        Context = "from-internal",
        Codecs = "ulaw,alaw",
        DtmfMode = "rfc4733",
        Transport = "udp",
        ForceRport = true,
        DirectMedia = false,
    };
}
