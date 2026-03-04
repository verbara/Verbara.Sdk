using DashboardExample.Models;
using FluentAssertions;

namespace DashboardExample.Tests.Models;

public class TrunkConfigTests
{
    [Fact]
    public void ToPjsipEndpoint_ShouldContainRequiredFields()
    {
        var config = CreateDefaultConfig();
        var endpoint = config.ToPjsipEndpoint();

        endpoint["type"].Should().Be("endpoint");
        endpoint["context"].Should().Be("from-trunk");
        endpoint["disallow"].Should().Be("all");
        endpoint["allow"].Should().Be("ulaw,alaw");
        endpoint["aors"].Should().Be("test-trunk-aor");
        endpoint["auth"].Should().Be("test-trunk-auth");
        endpoint["outbound_auth"].Should().Be("test-trunk-auth");
    }

    [Fact]
    public void ToPjsipAuth_ShouldContainCredentials()
    {
        var config = CreateDefaultConfig();
        var auth = config.ToPjsipAuth();

        auth["type"].Should().Be("auth");
        auth["auth_type"].Should().Be("userpass");
        auth["username"].Should().Be("testuser");
        auth["password"].Should().Be("testsecret");
    }

    [Fact]
    public void ToPjsipAor_ShouldContainContact()
    {
        var config = CreateDefaultConfig();
        var aor = config.ToPjsipAor();

        aor["type"].Should().Be("aor");
        aor["contact"].Should().Be("sip:10.0.0.1:5060");
        aor["qualify_frequency"].Should().Be("60");
    }

    [Fact]
    public void ToPjsipRegistration_ShouldReturnValues_WhenEnabled()
    {
        var config = CreateDefaultConfig();
        config.RegistrationEnabled = true;

        var reg = config.ToPjsipRegistration();

        reg.Should().NotBeNull();
        reg!["type"].Should().Be("registration");
        reg["server_uri"].Should().Be("sip:10.0.0.1:5060");
        reg["client_uri"].Should().Be("sip:testuser@10.0.0.1:5060");
        reg["outbound_auth"].Should().Be("test-trunk-auth");
    }

    [Fact]
    public void ToPjsipRegistration_ShouldReturnNull_WhenDisabled()
    {
        var config = CreateDefaultConfig();
        config.RegistrationEnabled = false;

        config.ToPjsipRegistration().Should().BeNull();
    }

    [Fact]
    public void RoundTrip_PjsipSections()
    {
        var original = CreateDefaultConfig();

        var endpoint = original.ToPjsipEndpoint();
        var auth = original.ToPjsipAuth();
        var aor = original.ToPjsipAor();
        var reg = original.ToPjsipRegistration();

        var restored = TrunkConfig.FromPjsipSections("test-trunk", endpoint, auth, aor, reg);

        restored.Name.Should().Be("test-trunk");
        restored.Technology.Should().Be(TrunkTechnology.PjSip);
        restored.Host.Should().Be("10.0.0.1");
        restored.Port.Should().Be(5060);
        restored.Username.Should().Be("testuser");
        restored.Secret.Should().Be("testsecret");
        restored.Context.Should().Be("from-trunk");
        restored.Codecs.Should().Be("ulaw,alaw");
        restored.RegistrationEnabled.Should().BeTrue();
    }

    [Fact]
    public void FromPjsipSections_ShouldHandleNoRegistration()
    {
        var endpoint = new Dictionary<string, string>
        {
            ["type"] = "endpoint",
            ["context"] = "internal",
        };

        var config = TrunkConfig.FromPjsipSections("trunk1", endpoint, null, null, null);

        config.RegistrationEnabled.Should().BeFalse();
        config.Context.Should().Be("internal");
    }

    [Fact]
    public void ToSipPeer_ShouldContainRequiredFields()
    {
        var config = CreateDefaultConfig();
        config.Technology = TrunkTechnology.Sip;

        var peer = config.ToSipPeer();

        peer["type"].Should().Be("peer");
        peer["host"].Should().Be("10.0.0.1");
        peer["port"].Should().Be("5060");
        peer["username"].Should().Be("testuser");
        peer["secret"].Should().Be("testsecret");
        peer["context"].Should().Be("from-trunk");
    }

    [Fact]
    public void RoundTrip_SipPeer()
    {
        var original = CreateDefaultConfig();
        original.Technology = TrunkTechnology.Sip;

        var section = original.ToSipPeer();
        var restored = TrunkConfig.FromSipPeer("test-trunk", section);

        restored.Name.Should().Be("test-trunk");
        restored.Technology.Should().Be(TrunkTechnology.Sip);
        restored.Host.Should().Be("10.0.0.1");
        restored.Username.Should().Be("testuser");
        restored.Secret.Should().Be("testsecret");
    }

    [Fact]
    public void ToIaxPeer_ShouldContainRequiredFields()
    {
        var config = CreateDefaultConfig();
        config.Technology = TrunkTechnology.Iax2;
        config.Port = 4569;

        var peer = config.ToIaxPeer();

        peer["type"].Should().Be("peer");
        peer["host"].Should().Be("10.0.0.1");
        peer["trunk"].Should().Be("yes");
    }

    [Fact]
    public void RoundTrip_IaxPeer()
    {
        var original = CreateDefaultConfig();
        original.Technology = TrunkTechnology.Iax2;
        original.Port = 4569;

        var section = original.ToIaxPeer();
        var restored = TrunkConfig.FromIaxPeer("test-trunk", section);

        restored.Name.Should().Be("test-trunk");
        restored.Technology.Should().Be(TrunkTechnology.Iax2);
        restored.Host.Should().Be("10.0.0.1");
        restored.Port.Should().Be(4569);
    }

    [Fact]
    public void ToPjsipEndpoint_ShouldIncludeMaxChannels_WhenSet()
    {
        var config = CreateDefaultConfig();
        config.MaxChannels = 10;

        var endpoint = config.ToPjsipEndpoint();

        endpoint["device_state_busy_at"].Should().Be("10");
    }

    [Fact]
    public void ToPjsipEndpoint_ShouldNotIncludeMaxChannels_WhenZero()
    {
        var config = CreateDefaultConfig();
        config.MaxChannels = 0;

        var endpoint = config.ToPjsipEndpoint();

        endpoint.Should().NotContainKey("device_state_busy_at");
    }

    [Fact]
    public void Codecs_ShouldRoundTrip()
    {
        var config = CreateDefaultConfig();
        config.Codecs = "g729,opus,g722";

        var endpoint = config.ToPjsipEndpoint();
        endpoint["allow"].Should().Be("g729,opus,g722");

        var restored = TrunkConfig.FromPjsipSections("test", endpoint, null, null, null);
        restored.Codecs.Should().Be("g729,opus,g722");
    }

    private static TrunkConfig CreateDefaultConfig() => new()
    {
        Name = "test-trunk",
        Technology = TrunkTechnology.PjSip,
        Host = "10.0.0.1",
        Port = 5060,
        Transport = "udp",
        Username = "testuser",
        Secret = "testsecret",
        AuthType = "userpass",
        Context = "from-trunk",
        Codecs = "ulaw,alaw",
        DtmfMode = "rfc4733",
        ForceRport = true,
        Comedia = true,
        QualifyFrequency = 60,
        RegistrationEnabled = true,
    };
}
