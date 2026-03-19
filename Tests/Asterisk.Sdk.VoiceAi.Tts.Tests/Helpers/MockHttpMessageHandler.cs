using System.Net;

namespace Asterisk.Sdk.VoiceAi.Tts.Tests.Helpers;

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly byte[] _responseBytes;
    private readonly HttpStatusCode _status;

    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Content body captured eagerly during SendAsync (survives request disposal).</summary>
    public string? LastRequestBody { get; private set; }

    public MockHttpMessageHandler(byte[] responseBytes, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseBytes = responseBytes;
        _status = status;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new HttpResponseMessage(_status)
        {
            Content = new ByteArrayContent(_responseBytes)
        };
    }
}
