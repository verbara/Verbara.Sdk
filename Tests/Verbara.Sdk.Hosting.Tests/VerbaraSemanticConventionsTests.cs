using Verbara.Sdk.Hosting;
using FluentAssertions;

namespace Verbara.Sdk.Hosting.Tests;

/// <summary>
/// Pins the wire-level attribute names exposed by <see cref="VerbaraSemanticConventions"/>
/// so a refactor cannot silently rename a label and break consumer dashboards.
/// The values mirror the proposal in
/// <c>docs/research/2026-04-19-otel-sip-semantic-conventions.md</c>.
/// </summary>
public sealed class VerbaraSemanticConventionsTests
{
    [Theory]
    [InlineData("asterisk.server.name")]
    [InlineData("asterisk.server.version")]
    [InlineData("asterisk.server.hostname")]
    [InlineData("asterisk.sdk.version")]
    public void Resource_NamesShouldMatchDraft(string expected)
    {
        var actual = expected switch
        {
            "asterisk.server.name"     => VerbaraSemanticConventions.Resource.ServerName,
            "asterisk.server.version"  => VerbaraSemanticConventions.Resource.ServerVersion,
            "asterisk.server.hostname" => VerbaraSemanticConventions.Resource.ServerHostname,
            "asterisk.sdk.version"     => VerbaraSemanticConventions.Resource.SdkVersion,
            _ => throw new InvalidOperationException($"Unmapped: {expected}")
        };
        actual.Should().Be(expected);
    }

    [Fact]
    public void Channel_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.Channel.Id.Should().Be("asterisk.channel.id");
        VerbaraSemanticConventions.Channel.Name.Should().Be("asterisk.channel.name");
        VerbaraSemanticConventions.Channel.State.Should().Be("asterisk.channel.state");
        VerbaraSemanticConventions.Channel.Driver.Should().Be("asterisk.channel.driver");
    }

    [Fact]
    public void Bridge_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.Bridge.Id.Should().Be("asterisk.bridge.id");
        VerbaraSemanticConventions.Bridge.Name.Should().Be("asterisk.bridge.name");
        VerbaraSemanticConventions.Bridge.Type.Should().Be("asterisk.bridge.type");
        VerbaraSemanticConventions.Bridge.ChannelCount.Should().Be("asterisk.bridge.channel_count");
    }

    [Fact]
    public void Calls_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.Calls.Id.Should().Be("call.id");
        VerbaraSemanticConventions.Calls.Direction.Should().Be("call.direction");
        VerbaraSemanticConventions.Calls.State.Should().Be("call.state");
        VerbaraSemanticConventions.Calls.DurationMs.Should().Be("call.duration_ms");
    }

    [Fact]
    public void Dialplan_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.Dialplan.Context.Should().Be("dialplan.context");
        VerbaraSemanticConventions.Dialplan.Extension.Should().Be("dialplan.extension");
        VerbaraSemanticConventions.Dialplan.Priority.Should().Be("dialplan.priority");
        VerbaraSemanticConventions.Dialplan.Application.Should().Be("dialplan.application");
    }

    [Fact]
    public void Sip_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.Sip.CallId.Should().Be("sip.call_id");
        VerbaraSemanticConventions.Sip.Method.Should().Be("sip.method");
        VerbaraSemanticConventions.Sip.ResponseCode.Should().Be("sip.response_code");
        VerbaraSemanticConventions.Sip.ResponsePhrase.Should().Be("sip.response_phrase");
        VerbaraSemanticConventions.Sip.FromUri.Should().Be("sip.from_uri");
        VerbaraSemanticConventions.Sip.ToUri.Should().Be("sip.to_uri");
        VerbaraSemanticConventions.Sip.UserAgent.Should().Be("sip.user_agent");
        VerbaraSemanticConventions.Sip.Transport.Should().Be("sip.transport");
    }

    [Fact]
    public void Media_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.Media.Codec.Should().Be("media.codec");
        VerbaraSemanticConventions.Media.SampleRate.Should().Be("media.sample_rate");
        VerbaraSemanticConventions.Media.Direction.Should().Be("media.direction");
        VerbaraSemanticConventions.Media.BitrateBps.Should().Be("media.bitrate_bps");
        VerbaraSemanticConventions.Media.FramesReceived.Should().Be("media.frames_received");
        VerbaraSemanticConventions.Media.FramesLost.Should().Be("media.frames_lost");
    }

    [Fact]
    public void Queues_And_Agent_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.Queues.Name.Should().Be("asterisk.queue.name");
        VerbaraSemanticConventions.Queues.Strategy.Should().Be("asterisk.queue.strategy");
        VerbaraSemanticConventions.Queues.WaitMs.Should().Be("asterisk.queue.wait_ms");
        VerbaraSemanticConventions.Agent.Id.Should().Be("asterisk.agent.id");
        VerbaraSemanticConventions.Agent.State.Should().Be("asterisk.agent.state");
    }

    [Fact]
    public void VoiceAi_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.VoiceAi.Provider.Should().Be("voiceai.provider");
        VerbaraSemanticConventions.VoiceAi.Operation.Should().Be("voiceai.operation");
        VerbaraSemanticConventions.VoiceAi.Model.Should().Be("voiceai.model");
        VerbaraSemanticConventions.VoiceAi.Language.Should().Be("voiceai.language");
        VerbaraSemanticConventions.VoiceAi.LatencyTtftMs.Should().Be("voiceai.latency.ttft_ms");
        VerbaraSemanticConventions.VoiceAi.LatencyTtfbMs.Should().Be("voiceai.latency.ttfb_ms");
        VerbaraSemanticConventions.VoiceAi.TokensInput.Should().Be("voiceai.tokens.input");
        VerbaraSemanticConventions.VoiceAi.TokensOutput.Should().Be("voiceai.tokens.output");
        VerbaraSemanticConventions.VoiceAi.AudioDurationMs.Should().Be("voiceai.audio.duration_ms");
        VerbaraSemanticConventions.VoiceAi.Interrupted.Should().Be("voiceai.interrupted");
    }

    [Fact]
    public void Events_NamesShouldMatchDraft()
    {
        VerbaraSemanticConventions.Events.ChannelHangup.Should().Be("asterisk.channel.hangup");
        VerbaraSemanticConventions.Events.DtmfReceived.Should().Be("asterisk.dtmf.received");
        VerbaraSemanticConventions.Events.MediaStarted.Should().Be("asterisk.media.started");
        VerbaraSemanticConventions.Events.MediaBuffering.Should().Be("asterisk.media.buffering");
        VerbaraSemanticConventions.Events.MediaMarkProcessed.Should().Be("asterisk.media.mark_processed");
    }

    [Fact]
    public void AllNames_ShouldBeSnakeCase_WithKnownPrefixes()
    {
        var prefixes = new[] { "asterisk.", "call.", "dialplan.", "sip.", "media.", "voiceai." };
        foreach (var name in AllConstNames())
        {
            prefixes.Should().Contain(p => name.StartsWith(p, StringComparison.Ordinal),
                because: $"'{name}' should start with one of the standardized prefixes");
            name.ToLowerInvariant().Should().Be(name,
                because: $"'{name}' should be all lowercase per the snake_case convention");
        }
    }

    private static IEnumerable<string> AllConstNames() =>
    [
        VerbaraSemanticConventions.Resource.ServerName, VerbaraSemanticConventions.Resource.ServerVersion,
        VerbaraSemanticConventions.Resource.ServerHostname, VerbaraSemanticConventions.Resource.SdkVersion,
        VerbaraSemanticConventions.Channel.Id, VerbaraSemanticConventions.Channel.Name,
        VerbaraSemanticConventions.Channel.State, VerbaraSemanticConventions.Channel.Driver,
        VerbaraSemanticConventions.Bridge.Id, VerbaraSemanticConventions.Bridge.Name,
        VerbaraSemanticConventions.Bridge.Type, VerbaraSemanticConventions.Bridge.ChannelCount,
        VerbaraSemanticConventions.Calls.Id, VerbaraSemanticConventions.Calls.Direction,
        VerbaraSemanticConventions.Calls.State, VerbaraSemanticConventions.Calls.DurationMs,
        VerbaraSemanticConventions.Dialplan.Context, VerbaraSemanticConventions.Dialplan.Extension,
        VerbaraSemanticConventions.Dialplan.Priority, VerbaraSemanticConventions.Dialplan.Application,
        VerbaraSemanticConventions.Sip.CallId, VerbaraSemanticConventions.Sip.Method,
        VerbaraSemanticConventions.Sip.ResponseCode, VerbaraSemanticConventions.Sip.ResponsePhrase,
        VerbaraSemanticConventions.Sip.FromUri, VerbaraSemanticConventions.Sip.ToUri,
        VerbaraSemanticConventions.Sip.UserAgent, VerbaraSemanticConventions.Sip.Transport,
        VerbaraSemanticConventions.Media.Codec, VerbaraSemanticConventions.Media.SampleRate,
        VerbaraSemanticConventions.Media.Direction, VerbaraSemanticConventions.Media.BitrateBps,
        VerbaraSemanticConventions.Media.FramesReceived, VerbaraSemanticConventions.Media.FramesLost,
        VerbaraSemanticConventions.Queues.Name, VerbaraSemanticConventions.Queues.Strategy,
        VerbaraSemanticConventions.Queues.WaitMs,
        VerbaraSemanticConventions.Agent.Id, VerbaraSemanticConventions.Agent.State,
        VerbaraSemanticConventions.VoiceAi.Provider, VerbaraSemanticConventions.VoiceAi.Operation,
        VerbaraSemanticConventions.VoiceAi.Model, VerbaraSemanticConventions.VoiceAi.Language,
        VerbaraSemanticConventions.VoiceAi.LatencyTtftMs, VerbaraSemanticConventions.VoiceAi.LatencyTtfbMs,
        VerbaraSemanticConventions.VoiceAi.TokensInput, VerbaraSemanticConventions.VoiceAi.TokensOutput,
        VerbaraSemanticConventions.VoiceAi.AudioDurationMs, VerbaraSemanticConventions.VoiceAi.Interrupted,
        VerbaraSemanticConventions.Events.ChannelHangup, VerbaraSemanticConventions.Events.DtmfReceived,
        VerbaraSemanticConventions.Events.MediaStarted, VerbaraSemanticConventions.Events.MediaBuffering,
        VerbaraSemanticConventions.Events.MediaMarkProcessed,
    ];
}
