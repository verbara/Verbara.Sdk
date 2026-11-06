namespace Verbara.Sdk;

/// <summary>
/// The parameters of <c>POST /channels/externalMedia</c>, by name, for
/// <see cref="IAriChannelsResource.CreateExternalMediaAsync(AriExternalMediaRequest, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// A record rather than more optional parameters on the shipped overload: every optional there is a <c>string?</c> in
/// a fixed position, so a parameter added among them could rebind an existing positional call to another meaning and
/// still compile. A named property cannot.
/// </para>
/// <para>
/// With <see cref="TransportData"/> <see langword="null"/> and <see cref="Variables"/> <see langword="null"/> or empty,
/// the request is the one the shipped overload sends for the same values.
/// </para>
/// </remarks>
public sealed record AriExternalMediaRequest
{
    /// <summary>The Stasis application the channel is placed in (<c>app</c>).</summary>
    public required string App { get; init; }

    /// <summary>
    /// <c>external_host</c>: the <c>host:port</c> Asterisk connects to, or, with <c>transport=websocket</c>, the name of
    /// the <c>websocket_client.conf</c> connection.
    /// </summary>
    public required string ExternalHost { get; init; }

    /// <summary>The media format (<c>format</c>), for example <c>slin16</c> or <c>ulaw</c>.</summary>
    public required string Format { get; init; }

    /// <summary><c>encapsulation</c>, or <see langword="null"/> to send none.</summary>
    public string? Encapsulation { get; init; }

    /// <summary><c>transport</c>, or <see langword="null"/> to send none.</summary>
    public string? Transport { get; init; }

    /// <summary><c>connection_type</c>, or <see langword="null"/> to send none.</summary>
    public string? ConnectionType { get; init; }

    /// <summary><c>direction</c>, or <see langword="null"/> to send none.</summary>
    public string? Direction { get; init; }

    /// <summary>
    /// <c>data</c>, or <see langword="null"/> to send none. Under AudioSocket encapsulation it is the identification
    /// UUID Asterisk sends on the audio connection; it is not the channel id.
    /// </summary>
    public string? Data { get; init; }

    /// <summary>
    /// The id Asterisk gives the channel it creates (<c>channelId</c>, camelCase on the wire), returned as
    /// <see cref="AriChannel.Id"/>; <see langword="null"/> lets Asterisk mint one.
    /// </summary>
    public string? ChannelId { get; init; }

    /// <summary>
    /// <c>transport_data</c>, sent as given; <see langword="null"/> to send none. Its grammar is Asterisk's dial-option
    /// grammar: the SDK neither parses nor validates it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured with <c>transport=websocket</c> only. <c>f(json)</c>: from 20.20, the connection's <c>MEDIA_START</c> and
    /// control messages are JSON, and its <c>channel_variables</c> carry the channel's variables; without it they are
    /// plain text, which carries none. <c>u</c>: not recognised on 20.20, 22.11 and 23.5 (the create returns HTTP 200
    /// and Asterisk logs a warning). <c>p</c>: no warning on those versions, its effect not measured there. Both are
    /// documented from 22.12 and 23.6.
    /// </para>
    /// <para>
    /// Implementations written before this property existed throw <see cref="NotSupportedException"/> when it is set,
    /// rather than drop it.
    /// </para>
    /// </remarks>
    public string? TransportData { get; init; }

    /// <summary>
    /// Channel variables, sent in the request body as <c>{"variables":{…}}</c>; <see langword="null"/> or empty to send
    /// no body. Measured with <c>transport=websocket</c>: each variable is readable on the created channel, and appears
    /// in <c>MEDIA_START</c>'s <c>channel_variables</c> on an <c>f(json)</c> connection only.
    /// </summary>
    /// <remarks>
    /// Implementations written before this property existed throw <see cref="NotSupportedException"/> when it holds an
    /// entry, rather than drop it.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Variables { get; init; }
}
