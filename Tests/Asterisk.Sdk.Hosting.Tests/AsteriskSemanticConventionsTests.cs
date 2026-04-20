using Asterisk.Sdk.Hosting;
using FluentAssertions;

namespace Asterisk.Sdk.Hosting.Tests;

/// <summary>
/// Pins the wire-level attribute names exposed by <see cref="AsteriskSemanticConventions"/>
/// so a refactor cannot silently rename a label and break consumer dashboards.
/// The values mirror the proposal in
/// <c>docs/research/2026-04-19-otel-sip-semantic-conventions.md</c>.
/// </summary>
public sealed class AsteriskSemanticConventionsTests
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
            "asterisk.server.name"     => AsteriskSemanticConventions.Resource.ServerName,
            "asterisk.server.version"  => AsteriskSemanticConventions.Resource.ServerVersion,
            "asterisk.server.hostname" => AsteriskSemanticConventions.Resource.ServerHostname,
            "asterisk.sdk.version"     => AsteriskSemanticConventions.Resource.SdkVersion,
            _ => throw new InvalidOperationException($"Unmapped: {expected}")
        };
        actual.Should().Be(expected);
    }

    [Fact]
    public void Channel_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.Channel.Id.Should().Be("asterisk.channel.id");
        AsteriskSemanticConventions.Channel.Name.Should().Be("asterisk.channel.name");
        AsteriskSemanticConventions.Channel.State.Should().Be("asterisk.channel.state");
        AsteriskSemanticConventions.Channel.Driver.Should().Be("asterisk.channel.driver");
    }

    [Fact]
    public void Bridge_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.Bridge.Id.Should().Be("asterisk.bridge.id");
        AsteriskSemanticConventions.Bridge.Name.Should().Be("asterisk.bridge.name");
        AsteriskSemanticConventions.Bridge.Type.Should().Be("asterisk.bridge.type");
        AsteriskSemanticConventions.Bridge.ChannelCount.Should().Be("asterisk.bridge.channel_count");
    }

    [Fact]
    public void Calls_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.Calls.Id.Should().Be("call.id");
        AsteriskSemanticConventions.Calls.Direction.Should().Be("call.direction");
        AsteriskSemanticConventions.Calls.State.Should().Be("call.state");
        AsteriskSemanticConventions.Calls.DurationMs.Should().Be("call.duration_ms");
    }

    [Fact]
    public void Dialplan_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.Dialplan.Context.Should().Be("dialplan.context");
        AsteriskSemanticConventions.Dialplan.Extension.Should().Be("dialplan.extension");
        AsteriskSemanticConventions.Dialplan.Priority.Should().Be("dialplan.priority");
        AsteriskSemanticConventions.Dialplan.Application.Should().Be("dialplan.application");
    }

    [Fact]
    public void Sip_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.Sip.CallId.Should().Be("sip.call_id");
        AsteriskSemanticConventions.Sip.Method.Should().Be("sip.method");
        AsteriskSemanticConventions.Sip.ResponseCode.Should().Be("sip.response_code");
        AsteriskSemanticConventions.Sip.ResponsePhrase.Should().Be("sip.response_phrase");
        AsteriskSemanticConventions.Sip.FromUri.Should().Be("sip.from_uri");
        AsteriskSemanticConventions.Sip.ToUri.Should().Be("sip.to_uri");
        AsteriskSemanticConventions.Sip.UserAgent.Should().Be("sip.user_agent");
        AsteriskSemanticConventions.Sip.Transport.Should().Be("sip.transport");
    }

    [Fact]
    public void Media_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.Media.Codec.Should().Be("media.codec");
        AsteriskSemanticConventions.Media.SampleRate.Should().Be("media.sample_rate");
        AsteriskSemanticConventions.Media.Direction.Should().Be("media.direction");
        AsteriskSemanticConventions.Media.BitrateBps.Should().Be("media.bitrate_bps");
        AsteriskSemanticConventions.Media.FramesReceived.Should().Be("media.frames_received");
        AsteriskSemanticConventions.Media.FramesLost.Should().Be("media.frames_lost");
    }

    [Fact]
    public void Queues_And_Agent_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.Queues.Name.Should().Be("asterisk.queue.name");
        AsteriskSemanticConventions.Queues.Strategy.Should().Be("asterisk.queue.strategy");
        AsteriskSemanticConventions.Queues.WaitMs.Should().Be("asterisk.queue.wait_ms");
        AsteriskSemanticConventions.Agent.Id.Should().Be("asterisk.agent.id");
        AsteriskSemanticConventions.Agent.State.Should().Be("asterisk.agent.state");
    }

    [Fact]
    public void VoiceAi_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.VoiceAi.Provider.Should().Be("voiceai.provider");
        AsteriskSemanticConventions.VoiceAi.Operation.Should().Be("voiceai.operation");
        AsteriskSemanticConventions.VoiceAi.Model.Should().Be("voiceai.model");
        AsteriskSemanticConventions.VoiceAi.Language.Should().Be("voiceai.language");
        AsteriskSemanticConventions.VoiceAi.LatencyTtftMs.Should().Be("voiceai.latency.ttft_ms");
        AsteriskSemanticConventions.VoiceAi.LatencyTtfbMs.Should().Be("voiceai.latency.ttfb_ms");
        AsteriskSemanticConventions.VoiceAi.TokensInput.Should().Be("voiceai.tokens.input");
        AsteriskSemanticConventions.VoiceAi.TokensOutput.Should().Be("voiceai.tokens.output");
        AsteriskSemanticConventions.VoiceAi.AudioDurationMs.Should().Be("voiceai.audio.duration_ms");
        AsteriskSemanticConventions.VoiceAi.Interrupted.Should().Be("voiceai.interrupted");
    }

    [Fact]
    public void Events_NamesShouldMatchDraft()
    {
        AsteriskSemanticConventions.Events.ChannelHangup.Should().Be("asterisk.channel.hangup");
        AsteriskSemanticConventions.Events.DtmfReceived.Should().Be("asterisk.dtmf.received");
        AsteriskSemanticConventions.Events.MediaStarted.Should().Be("asterisk.media.started");
        AsteriskSemanticConventions.Events.MediaBuffering.Should().Be("asterisk.media.buffering");
        AsteriskSemanticConventions.Events.MediaMarkProcessed.Should().Be("asterisk.media.mark_processed");
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
        AsteriskSemanticConventions.Resource.ServerName, AsteriskSemanticConventions.Resource.ServerVersion,
        AsteriskSemanticConventions.Resource.ServerHostname, AsteriskSemanticConventions.Resource.SdkVersion,
        AsteriskSemanticConventions.Channel.Id, AsteriskSemanticConventions.Channel.Name,
        AsteriskSemanticConventions.Channel.State, AsteriskSemanticConventions.Channel.Driver,
        AsteriskSemanticConventions.Bridge.Id, AsteriskSemanticConventions.Bridge.Name,
        AsteriskSemanticConventions.Bridge.Type, AsteriskSemanticConventions.Bridge.ChannelCount,
        AsteriskSemanticConventions.Calls.Id, AsteriskSemanticConventions.Calls.Direction,
        AsteriskSemanticConventions.Calls.State, AsteriskSemanticConventions.Calls.DurationMs,
        AsteriskSemanticConventions.Dialplan.Context, AsteriskSemanticConventions.Dialplan.Extension,
        AsteriskSemanticConventions.Dialplan.Priority, AsteriskSemanticConventions.Dialplan.Application,
        AsteriskSemanticConventions.Sip.CallId, AsteriskSemanticConventions.Sip.Method,
        AsteriskSemanticConventions.Sip.ResponseCode, AsteriskSemanticConventions.Sip.ResponsePhrase,
        AsteriskSemanticConventions.Sip.FromUri, AsteriskSemanticConventions.Sip.ToUri,
        AsteriskSemanticConventions.Sip.UserAgent, AsteriskSemanticConventions.Sip.Transport,
        AsteriskSemanticConventions.Media.Codec, AsteriskSemanticConventions.Media.SampleRate,
        AsteriskSemanticConventions.Media.Direction, AsteriskSemanticConventions.Media.BitrateBps,
        AsteriskSemanticConventions.Media.FramesReceived, AsteriskSemanticConventions.Media.FramesLost,
        AsteriskSemanticConventions.Queues.Name, AsteriskSemanticConventions.Queues.Strategy,
        AsteriskSemanticConventions.Queues.WaitMs,
        AsteriskSemanticConventions.Agent.Id, AsteriskSemanticConventions.Agent.State,
        AsteriskSemanticConventions.VoiceAi.Provider, AsteriskSemanticConventions.VoiceAi.Operation,
        AsteriskSemanticConventions.VoiceAi.Model, AsteriskSemanticConventions.VoiceAi.Language,
        AsteriskSemanticConventions.VoiceAi.LatencyTtftMs, AsteriskSemanticConventions.VoiceAi.LatencyTtfbMs,
        AsteriskSemanticConventions.VoiceAi.TokensInput, AsteriskSemanticConventions.VoiceAi.TokensOutput,
        AsteriskSemanticConventions.VoiceAi.AudioDurationMs, AsteriskSemanticConventions.VoiceAi.Interrupted,
        AsteriskSemanticConventions.Events.ChannelHangup, AsteriskSemanticConventions.Events.DtmfReceived,
        AsteriskSemanticConventions.Events.MediaStarted, AsteriskSemanticConventions.Events.MediaBuffering,
        AsteriskSemanticConventions.Events.MediaMarkProcessed,
    ];
}
