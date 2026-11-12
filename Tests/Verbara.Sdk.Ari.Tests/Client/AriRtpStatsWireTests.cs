using System.Text.Json;
using FluentAssertions;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Resources;
using Verbara.Sdk.Ari.Tests.Resources;

namespace Verbara.Sdk.Ari.Tests.Client;

/// <summary>
/// <c>GET /channels/{id}/rtp_statistics</c> as Asterisk writes it. Asterisk writes <c>rtt</c> and the
/// <c>*rxploss</c> figures as JSON reals, and the SSRCs as unsigned 32-bit integers; a far end that is not
/// Asterisk can pick an SSRC from 2^31 up. Every body below is a captured reply, read through the SDK's own
/// JSON context, never one the SDK wrote.
/// </summary>
public sealed class AriRtpStatsWireTests
{
    // Captured verbatim from Asterisk 20.20.1 (PJSIP loop call answered in Stasis, 12 s of RTP, RTCP on).
    // The same shape, rtt and the loss figures as reals, came from 22.11.0, 23.5.0, 22.12.0-rc2, 23.6.0-rc2
    // and 24.0.0-rc2.
    internal const string CapturedLoopReply =
        """
        {"txcount":600,"rxcount":599,"txploss":0,"rxploss":0,"local_ssrc":2099359027,"remote_ssrc":589869326,"txoctetcount":96000,"rxoctetcount":95840,"channel_uniqueid":"lab-rtp-0","txjitter":0.0,"rxjitter":0.0,"remote_maxjitter":0.0,"remote_minjitter":0.0,"remote_normdevjitter":0.0,"remote_stdevjitter":0.0,"local_maxjitter":0.0,"local_minjitter":0.0,"local_normdevjitter":0.0,"local_stdevjitter":0.0,"remote_maxrxploss":0.0,"remote_minrxploss":0.0,"remote_normdevrxploss":0.0,"remote_stdevrxploss":0.0,"local_maxrxploss":0.0,"local_minrxploss":0.0,"local_normdevrxploss":0.0,"local_stdevrxploss":0.0,"rtt":6.0999999999999999e-5,"maxrtt":9.1000000000000003e-5,"minrtt":6.0999999999999999e-5,"normdevrtt":7.6000000000000004e-5,"stdevrtt":1.5000000000000004e-5,"txmes":88,"rxmes":88,"remote_maxmes":88.087135886029188,"remote_minmes":88.08698755312318,"remote_normdevmes":88.087061719576184,"remote_stdevmes":7.4166453003954302e-5,"local_maxmes":88.087887367500016,"local_minmes":88.08698755312318,"local_normdevmes":88.087437460311605,"local_stdevmes":0.00044990718842186084}
        """;

    // Captured verbatim from Asterisk 24.0.0-rc2, the same call.
    private const string CapturedLoopReply24 =
        """
        {"txcount":600,"rxcount":599,"txploss":0,"rxploss":0,"local_ssrc":1035118775,"remote_ssrc":336468876,"txoctetcount":96000,"rxoctetcount":95840,"channel_uniqueid":"lab-rtp-0","txjitter":0.0,"rxjitter":0.0,"remote_maxjitter":0.0,"remote_minjitter":0.0,"remote_normdevjitter":0.0,"remote_stdevjitter":0.0,"local_maxjitter":0.0,"local_minjitter":0.0,"local_normdevjitter":0.0,"local_stdevjitter":0.0,"remote_maxrxploss":0.0,"remote_minrxploss":0.0,"remote_normdevrxploss":0.0,"remote_stdevrxploss":0.0,"local_maxrxploss":0.0,"local_minrxploss":0.0,"local_normdevrxploss":0.0,"local_stdevrxploss":0.0,"rtt":9.1000000000000003e-5,"maxrtt":0.000106,"minrtt":9.1000000000000003e-5,"normdevrtt":9.8500000000000009e-5,"stdevrtt":7.5000000000000027e-6,"txmes":88,"rxmes":88,"remote_maxmes":88.087400401431253,"remote_minmes":88.08736332072975,"remote_normdevmes":88.087381861080502,"remote_stdevmes":1.854035075155025e-5,"local_maxmes":88.087887367500016,"local_minmes":88.08736332072975,"local_normdevmes":88.087625344114883,"local_stdevmes":0.00026202338513314771}
        """;

    // Captured verbatim from Asterisk 20.20.1: the far end is a test peer that sends SSRC 0xF0000001, which
    // Asterisk reports, unsigned, as remote_ssrc. rtt is 0.0: no RTCP round trip was measured on this leg.
    private const string CapturedPeerReply =
        """
        {"txcount":600,"rxcount":600,"txploss":0,"rxploss":0,"local_ssrc":282801893,"remote_ssrc":4026531841,"txoctetcount":96000,"rxoctetcount":96000,"channel_uniqueid":"lab-peer-0","txjitter":0.0,"rxjitter":0.0,"remote_maxjitter":0.0,"remote_minjitter":0.0,"remote_normdevjitter":0.0,"remote_stdevjitter":0.0,"local_maxjitter":0.0,"local_minjitter":0.0,"local_normdevjitter":0.0,"local_stdevjitter":0.0,"remote_maxrxploss":0.0,"remote_minrxploss":0.0,"remote_normdevrxploss":0.0,"remote_stdevrxploss":0.0,"local_maxrxploss":0.0,"local_minrxploss":0.0,"local_normdevrxploss":0.0,"local_stdevrxploss":0.0,"rtt":0.0,"maxrtt":0.0,"minrtt":0.0,"normdevrtt":0.0,"stdevrtt":0.0,"txmes":88,"rxmes":0,"remote_maxmes":0.0,"remote_minmes":0.0,"remote_normdevmes":0.0,"remote_stdevmes":0.0,"local_maxmes":88.087887367500016,"local_minmes":88.087887367500016,"local_normdevmes":88.087887367500016,"local_stdevmes":0.0}
        """;

    private static readonly AriClientOptions Options = new()
    {
        BaseUrl = "http://localhost:8088",
        Username = "admin",
        Password = "secret",
        Application = "testapp",
    };

    [Fact]
    public void Deserialize_ShouldBindTheCapturedLoopReply_WithRealRoundTripAndLossFigures()
    {
        var stats = JsonSerializer.Deserialize(CapturedLoopReply, AriJsonContext.Default.AriRtpStats);

        stats.Should().NotBeNull();
        ((double?)stats!.Rtt).Should().Be(6.0999999999999999e-5);
        ((double?)stats.LocalMaxrxploss).Should().Be(0.0);
        ((double?)stats.LocalMinrxploss).Should().Be(0.0);
        ((long?)stats.LocalSsrc).Should().Be(2099359027L);
        ((long?)stats.RemoteSsrc).Should().Be(589869326L);
        stats.Txcount.Should().Be(600);
        stats.Rxcount.Should().Be(599);
        stats.ChannelUniqueid.Should().Be("lab-rtp-0");
    }

    [Fact]
    public void Deserialize_ShouldBindTheCapturedLoopReplyOfAsterisk24()
    {
        var stats = JsonSerializer.Deserialize(CapturedLoopReply24, AriJsonContext.Default.AriRtpStats);

        ((double?)stats!.Rtt).Should().Be(9.1000000000000003e-5);
        ((long?)stats.LocalSsrc).Should().Be(1035118775L);
    }

    [Fact]
    public void Deserialize_ShouldBindAFarEndSsrcAbove2To31_AsItself()
    {
        var stats = JsonSerializer.Deserialize(CapturedPeerReply, AriJsonContext.Default.AriRtpStats);

        ((long?)stats!.RemoteSsrc).Should().Be(4026531841L);
        ((long?)stats.LocalSsrc).Should().Be(282801893L);
        ((double?)stats.Rtt).Should().Be(0.0);
    }

    [Fact]
    public async Task GetRtpStatisticsAsync_ShouldReturnTheCapturedReply()
    {
        using var handler = new FakeHttpHandler(CapturedPeerReply);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8088/ari/") };
        var sut = new AriChannelsResource(http, Options);

        var stats = await sut.GetRtpStatisticsAsync("lab-peer-0");

        stats.Txcount.Should().Be(600);
        stats.Rxcount.Should().Be(600);
        ((long?)stats.RemoteSsrc).Should().Be(4026531841L);
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRequestUri.Should().EndWith("channels/lab-peer-0/rtp_statistics");
    }

    [Theory]
    [InlineData(CapturedLoopReply)]
    [InlineData(CapturedLoopReply24)]
    [InlineData(CapturedPeerReply)]
    public void EveryKeyOfACapturedReply_ShouldNameAMemberOfTheModel(string body)
    {
        var members = AriJsonContext.Default.AriRtpStats.Properties
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(body);

        var unbound = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(name => !members.Contains(name))
            .ToList();

        unbound.Should().BeEmpty();
    }

    // The negative control: the hand-written body of the test this class supersedes binds, before and after.
    [Fact]
    public void Deserialize_ShouldBindAnIntegerRoundTripTime_AndSmallSsrcs()
    {
        const string json = """{"txcount":1000,"rxcount":980,"rtt":42,"local_ssrc":111111,"remote_ssrc":222222}""";

        var stats = JsonSerializer.Deserialize(json, AriJsonContext.Default.AriRtpStats);

        ((double?)stats!.Rtt).Should().Be(42);
        ((long?)stats.LocalSsrc).Should().Be(111111L);
        ((long?)stats.RemoteSsrc).Should().Be(222222L);
    }
}
