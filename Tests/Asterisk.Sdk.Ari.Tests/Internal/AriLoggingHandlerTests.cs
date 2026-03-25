using System.Net;
using Asterisk.Sdk.Ari.Internal;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Sdk.Ari.Tests.Internal;

public sealed class AriLoggingHandlerTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FakeHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }

    private static AriLoggingHandler CreateHandler(ILogger logger)
    {
        var handler = new AriLoggingHandler(logger);
        return handler;
    }

    [Fact]
    public async Task SendAsync_ShouldLogSuccessResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok")
        };

        var handler = new AriLoggingHandler(NullLogger.Instance);
        // Replace the inner handler
        var innerField = typeof(DelegatingHandler).GetProperty("InnerHandler");
        innerField!.SetValue(handler, new FakeHandler(response));

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8088") };
        var result = await client.GetAsync("/ari/channels");

        result.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendAsync_ShouldLogErrorResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Not Found")
        };

        var handler = new AriLoggingHandler(NullLogger.Instance);
        var innerField = typeof(DelegatingHandler).GetProperty("InnerHandler");
        innerField!.SetValue(handler, new FakeHandler(response));

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8088") };
        var result = await client.GetAsync("/ari/channels/nonexistent");

        result.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendAsync_ShouldLogServerErrorResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error")
        };

        var handler = new AriLoggingHandler(NullLogger.Instance);
        var innerField = typeof(DelegatingHandler).GetProperty("InnerHandler");
        innerField!.SetValue(handler, new FakeHandler(response));

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8088") };
        var result = await client.PostAsync("/ari/channels", null);

        result.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
