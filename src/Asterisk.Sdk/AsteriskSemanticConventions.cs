namespace Asterisk.Sdk;

/// <summary>
/// Standardized OpenTelemetry attribute names for Asterisk / SIP telephony.
/// Backed by the draft proposal in
/// <c>docs/research/2026-04-19-otel-sip-semantic-conventions.md</c>. Use the
/// const strings here on every <see cref="System.Diagnostics.Activity.SetTag"/>
/// or metric-label call so consumer dashboards remain stable across SDK versions.
/// </summary>
public static class AsteriskSemanticConventions
{
    /// <summary>Resource-level attributes (set once on the <c>Resource</c>, not per span).</summary>
    public static class Resource
    {
        /// <summary>Logical Asterisk node name. Matches <c>AsteriskServer.Name</c>.</summary>
        public const string ServerName = "asterisk.server.name";

        /// <summary>Asterisk version reported by <c>GetInfoAsync</c>.</summary>
        public const string ServerVersion = "asterisk.server.version";

        /// <summary>Kernel hostname of the Asterisk node.</summary>
        public const string ServerHostname = "asterisk.server.hostname";

        /// <summary>The <c>Asterisk.Sdk</c> package version.</summary>
        public const string SdkVersion = "asterisk.sdk.version";
    }

    /// <summary>Channel-level attributes.</summary>
    public static class Channel
    {
        /// <summary>Asterisk channel id (e.g. <c>PJSIP/alice-00000042</c>).</summary>
        public const string Id = "asterisk.channel.id";

        /// <summary>Channel name (often equal to id on modern Asterisk).</summary>
        public const string Name = "asterisk.channel.name";

        /// <summary>Channel state (<c>Up</c>, <c>Ring</c>, <c>Busy</c>, <c>Hangup</c>, ...).</summary>
        public const string State = "asterisk.channel.state";

        /// <summary>Channel driver (<c>PJSIP</c>, <c>AudioSocket</c>, <c>WebSocket</c>, ...).</summary>
        public const string Driver = "asterisk.channel.driver";
    }

    /// <summary>Bridge-level attributes.</summary>
    public static class Bridge
    {
        /// <summary>Bridge UUID.</summary>
        public const string Id = "asterisk.bridge.id";

        /// <summary>Optional human bridge name.</summary>
        public const string Name = "asterisk.bridge.name";

        /// <summary>Bridge technology (<c>mixing</c>, <c>holding</c>, <c>meetme</c>, ...).</summary>
        public const string Type = "asterisk.bridge.type";

        /// <summary>Snapshot of channels in the bridge at span emission.</summary>
        public const string ChannelCount = "asterisk.bridge.channel_count";
    }

    /// <summary>Call-level correlation attributes.</summary>
    public static class Calls
    {
        /// <summary>
        /// Call correlation key (Asterisk LinkedID). Stable across bridge / transfer.
        /// Distinct from W3C <c>trace.id</c> — this is the call domain identifier.
        /// </summary>
        public const string Id = "call.id";

        /// <summary>Call direction (<c>inbound</c>, <c>outbound</c>, <c>internal</c>).</summary>
        public const string Direction = "call.direction";

        /// <summary>High-level call lifecycle state.</summary>
        public const string State = "call.state";

        /// <summary>Total call duration in milliseconds (only on completion spans).</summary>
        public const string DurationMs = "call.duration_ms";
    }

    /// <summary>Dialplan execution attributes.</summary>
    public static class Dialplan
    {
        /// <summary>Dialplan context the channel is currently in.</summary>
        public const string Context = "dialplan.context";

        /// <summary>Dialplan extension being processed.</summary>
        public const string Extension = "dialplan.extension";

        /// <summary>Dialplan priority step.</summary>
        public const string Priority = "dialplan.priority";

        /// <summary>Current dialplan application (<c>Dial</c>, <c>Queue</c>, <c>Stasis</c>, ...).</summary>
        public const string Application = "dialplan.application";
    }

    /// <summary>SIP signalling attributes.</summary>
    public static class Sip
    {
        /// <summary>SIP Call-ID header (signalling dialog id, distinct from <see cref="Calls.Id"/>).</summary>
        public const string CallId = "sip.call_id";

        /// <summary>SIP method (<c>INVITE</c>, <c>BYE</c>, <c>REGISTER</c>, ...).</summary>
        public const string Method = "sip.method";

        /// <summary>SIP response code (only on response spans).</summary>
        public const string ResponseCode = "sip.response_code";

        /// <summary>SIP response phrase (e.g. <c>OK</c>, <c>Not Found</c>).</summary>
        public const string ResponsePhrase = "sip.response_phrase";

        /// <summary>Caller URI from the SIP From header.</summary>
        public const string FromUri = "sip.from_uri";

        /// <summary>Callee URI from the SIP To header.</summary>
        public const string ToUri = "sip.to_uri";

        /// <summary>Caller's User-Agent header.</summary>
        public const string UserAgent = "sip.user_agent";

        /// <summary>SIP transport (<c>udp</c>, <c>tcp</c>, <c>tls</c>, <c>ws</c>, <c>wss</c>).</summary>
        public const string Transport = "sip.transport";
    }

    /// <summary>Media (RTP / codec / jitter) attributes.</summary>
    public static class Media
    {
        /// <summary>Negotiated codec (<c>opus</c>, <c>ulaw</c>, <c>alaw</c>, <c>g722</c>, <c>slin16</c>, ...).</summary>
        public const string Codec = "media.codec";

        /// <summary>Audio sample rate in Hz.</summary>
        public const string SampleRate = "media.sample_rate";

        /// <summary>Media direction (<c>in</c>, <c>out</c>, <c>both</c>).</summary>
        public const string Direction = "media.direction";

        /// <summary>Bitrate in bits per second.</summary>
        public const string BitrateBps = "media.bitrate_bps";

        /// <summary>Frames received during the span.</summary>
        public const string FramesReceived = "media.frames_received";

        /// <summary>Frames lost during the span.</summary>
        public const string FramesLost = "media.frames_lost";
    }

    /// <summary>Queue / agent attributes.</summary>
    public static class Queues
    {
        /// <summary>Queue name.</summary>
        public const string Name = "asterisk.queue.name";

        /// <summary>Queue strategy (<c>leastrecent</c>, <c>fewestcalls</c>, <c>ringall</c>, ...).</summary>
        public const string Strategy = "asterisk.queue.strategy";

        /// <summary>Caller wait time in milliseconds at span emission.</summary>
        public const string WaitMs = "asterisk.queue.wait_ms";
    }

    /// <summary>Agent attributes.</summary>
    public static class Agent
    {
        /// <summary>Agent identifier.</summary>
        public const string Id = "asterisk.agent.id";

        /// <summary>Agent state (<c>ready</c>, <c>busy</c>, <c>unavailable</c>, <c>paused</c>).</summary>
        public const string State = "asterisk.agent.state";
    }

    /// <summary>Voice AI attributes.</summary>
    public static class VoiceAi
    {
        /// <summary>Provider name (<c>OpenAI</c>, <c>Deepgram</c>, <c>Cartesia</c>, ...). Mirrors <c>SpeechRecognizer.ProviderName</c>.</summary>
        public const string Provider = "voiceai.provider";

        /// <summary>AI operation type (<c>stt</c>, <c>tts</c>, <c>llm</c>, <c>vad</c>, <c>realtime</c>).</summary>
        public const string Operation = "voiceai.operation";

        /// <summary>Specific model name (<c>sonic-3</c>, <c>ink-whisper</c>, <c>nova-2</c>, ...).</summary>
        public const string Model = "voiceai.model";

        /// <summary>BCP-47 language tag.</summary>
        public const string Language = "voiceai.language";

        /// <summary>Time to first token / first audio in milliseconds.</summary>
        public const string LatencyTtftMs = "voiceai.latency.ttft_ms";

        /// <summary>Time to first byte of response in milliseconds.</summary>
        public const string LatencyTtfbMs = "voiceai.latency.ttfb_ms";

        /// <summary>LLM input token count.</summary>
        public const string TokensInput = "voiceai.tokens.input";

        /// <summary>LLM output token count.</summary>
        public const string TokensOutput = "voiceai.tokens.output";

        /// <summary>Audio length produced or consumed in milliseconds.</summary>
        public const string AudioDurationMs = "voiceai.audio.duration_ms";

        /// <summary>Whether the turn was cut short by barge-in.</summary>
        public const string Interrupted = "voiceai.interrupted";
    }
}
