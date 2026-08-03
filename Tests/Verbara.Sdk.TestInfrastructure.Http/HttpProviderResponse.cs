using System.Net;
using Microsoft.AspNetCore.Http;
using WireMock;
using WireMock.ResponseBuilders;
using WireMock.ResponseProviders;
using WireMock.Settings;

namespace Verbara.Sdk.TestInfrastructure.Http;

/// <summary>
/// A stub response for <see cref="HttpProviderMockServer"/>, including the shapes a canned
/// <c>HttpMessageHandler</c> cannot express: error statuses with a real body, and chunked bodies
/// delivered as several network writes (ADR-0041).
/// </summary>
public sealed class HttpProviderResponse
{
    /// <summary>Media type used when a caller does not name one.</summary>
    public const string JsonContentType = "application/json";

    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpStatusCode _statusCode;
    private readonly string? _contentType;
    private readonly string? _textBody;
    private readonly byte[]? _byteBody;
    private readonly IReadOnlyList<byte[]>? _chunks;
    private readonly TimeSpan _delayBetweenChunks;

    private HttpProviderResponse(
        HttpStatusCode statusCode,
        string? contentType,
        string? textBody = null,
        byte[]? byteBody = null,
        IReadOnlyList<byte[]>? chunks = null,
        TimeSpan delayBetweenChunks = default)
    {
        _statusCode = statusCode;
        _contentType = contentType;
        _textBody = textBody;
        _byteBody = byteBody;
        _chunks = chunks;
        _delayBetweenChunks = delayBetweenChunks;
    }

    /// <summary>A JSON body — the STT shape.</summary>
    public static HttpProviderResponse Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new HttpProviderResponse(statusCode, JsonContentType, textBody: json);
    }

    /// <summary>A text body under a caller-chosen media type (SSML replies, plain-text errors).</summary>
    public static HttpProviderResponse Text(string body, string contentType, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return new HttpProviderResponse(statusCode, contentType, textBody: body);
    }

    /// <summary>A binary body — the TTS shape.</summary>
    public static HttpProviderResponse Bytes(byte[] body, string contentType, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return new HttpProviderResponse(statusCode, contentType, byteBody: body);
    }

    /// <summary>
    /// An error status, optionally carrying the provider's error body. Distinct from
    /// <see cref="Json(string, HttpStatusCode)"/> only in intent — it reads at the call site as
    /// "this request fails".
    /// </summary>
    public static HttpProviderResponse Status(
        HttpStatusCode statusCode,
        string? body = null,
        string contentType = JsonContentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return new HttpProviderResponse(statusCode, body is null ? null : contentType, textBody: body);
    }

    /// <summary>
    /// A binary body written as several separate network chunks, so the client's streaming read
    /// loop actually loops. <paramref name="delayBetweenChunks"/> spaces the writes out for tests
    /// that need the reader to be mid-stream when something else happens (cancellation, abort).
    /// </summary>
    /// <remarks>
    /// Every WireMock response already goes out under <c>Transfer-Encoding: chunked</c>; what this
    /// adds is control over the chunk boundaries, which a single buffered body cannot give.
    /// </remarks>
    public static HttpProviderResponse ChunkedBytes(
        IEnumerable<byte[]> chunks,
        string contentType,
        TimeSpan? delayBetweenChunks = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var materialised = chunks.ToArray();
        if (materialised.Length == 0)
            throw new ArgumentException("A chunked response needs at least one chunk.", nameof(chunks));

        return new HttpProviderResponse(
            statusCode,
            contentType,
            chunks: materialised,
            delayBetweenChunks: delayBetweenChunks ?? TimeSpan.Zero);
    }

    /// <summary>Add a response header (provider rate-limit headers, <c>Retry-After</c>, …).</summary>
    public HttpProviderResponse WithHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        _headers[name] = value;
        return this;
    }

    /// <summary>Translate the stub into a WireMock response provider.</summary>
    internal IResponseProvider Build()
    {
        if (_chunks is not null)
        {
            return new ChunkedBinaryResponseProvider(
                _chunks, _contentType!, (int)_statusCode, _delayBetweenChunks, _headers);
        }

        var builder = Response.Create().WithStatusCode(_statusCode);

        if (_contentType is not null)
            builder = builder.WithHeader("Content-Type", _contentType);

        foreach (var (name, value) in _headers)
            builder = builder.WithHeader(name, value);

        if (_byteBody is not null)
            builder = builder.WithBody(_byteBody);
        else if (_textBody is not null)
            builder = builder.WithBody(_textBody);

        return builder;
    }
}

/// <summary>
/// Writes a binary body straight to the Kestrel response stream, one flushed write per chunk.
/// </summary>
/// <remarks>
/// WireMock's own streaming body (<c>WithSseBody</c>) is UTF-8 text — pushing codec bytes through
/// it corrupts every byte above 0x7F. Owning the write loop keeps the payload byte-exact while
/// still producing real chunked framing.
/// </remarks>
internal sealed class ChunkedBinaryResponseProvider(
    IReadOnlyList<byte[]> chunks,
    string contentType,
    int statusCode,
    TimeSpan delayBetweenChunks,
    IReadOnlyDictionary<string, string> headers) : IResponseProvider
{
    public async Task<(IResponseMessage Message, IMapping? Mapping)> ProvideResponseAsync(
        IMapping mapping,
        HttpContext context,
        IRequestMessage requestMessage,
        WireMockServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);

        var response = context.Response;
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        foreach (var (name, value) in headers)
            response.Headers[name] = value;

        foreach (var chunk in chunks)
        {
            await response.Body.WriteAsync(chunk, context.RequestAborted).ConfigureAwait(false);
            await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

            if (delayBetweenChunks > TimeSpan.Zero)
                // fence-allow: SIMULATED-WORK — reproduces the provider pacing its audio stream; opt-in and off by default.
                await Task.Delay(delayBetweenChunks, context.RequestAborted).ConfigureAwait(false);
        }

        await response.CompleteAsync().ConfigureAwait(false);

        return (new ResponseMessage { StatusCode = statusCode }, mapping);
    }
}
