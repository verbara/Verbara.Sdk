using System.Net;

namespace Asterisk.Sdk.VoiceAi.Stt.Tests.Helpers;

internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpResponseMessage _response;
    public HttpRequestMessage? LastRequest { get; private set; }
    public List<HttpRequestMessage> Requests { get; } = [];

    public MockHttpMessageHandler(string jsonBody, HttpStatusCode status = HttpStatusCode.OK)
        => _response = new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_response);
    }
}
