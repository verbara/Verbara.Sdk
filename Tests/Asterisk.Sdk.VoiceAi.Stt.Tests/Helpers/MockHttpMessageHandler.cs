using System.Net;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;
    public HttpRequestMessage? LastRequest { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Content body captured eagerly during SendAsync (survives request disposal).</summary>
    public string? LastRequestBody { get; private set; }

    public MockHttpMessageHandler(string jsonBody, HttpStatusCode status = HttpStatusCode.OK)
        => _response = new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        Requests.Add(request);
        LastRequestBody = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : null;
        cancellationToken.ThrowIfCancellationRequested();
        return _response;
    }
}
