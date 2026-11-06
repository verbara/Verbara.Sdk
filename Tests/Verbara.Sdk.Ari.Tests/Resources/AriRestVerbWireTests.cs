using System.Globalization;
using System.Net;
using System.Text;
using Verbara.Sdk.Ari.Client;
using Verbara.Sdk.Ari.Resources;
using FluentAssertions;

namespace Verbara.Sdk.Ari.Tests.Resources;

/// <summary>
/// Holds the channel hold, mute and unmute calls and the log-channel rotation to what Asterisk answers on the wire,
/// not to the SDK's own encoder: the fake replays Asterisk's captured responses, so a verb or a query Asterisk
/// rejects fails here exactly as it fails against a real server.
/// </summary>
public sealed class AriRestVerbWireTests
{
    // Captured verbatim from Asterisk 22.11.0 through a byte relay between the compiled SDK and ARI (lab capture
    // run 2026-11-06-a04e8a3e, image 22.11.0, lab commit 94e3d214, files raw/a22/sdkproxy/proxy/conn-002.s2c — the
    // SDK's own requests — and conn-003.s2c — the contrast requests). The same status lines and bodies were
    // measured on 20.20.1, 22.12.0-rc2, 23.5.0, 23.6.0-rc2 and 24.0.0-rc2.

    // conn-002.s2c @739: the answer to PUT /ari/channels/{id}/hold.
    private const string Hold405 =
        "HTTP/1.1 405 Method Not Allowed\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:19 GMT\r\nCache-Control: no-cache, no-store\r\nAllow: OPTIONS,POST,DELETE\r\nContent-type: application/json\r\nContent-Length: 28\r\n\r\n{\"message\":\"Invalid method\"}";

    // conn-003.s2c @144: the answer to POST /ari/channels/{id}/hold.
    private const string Hold204 =
        "HTTP/1.1 204 No Content\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:21 GMT\r\nCache-Control: no-cache, no-store\r\nContent-Length: 0\r\n\r\n";

    // conn-002.s2c @980: the answer to PUT /ari/channels/{id}/mute?direction=both.
    private const string Mute405 =
        "HTTP/1.1 405 Method Not Allowed\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:24 GMT\r\nCache-Control: no-cache, no-store\r\nAllow: OPTIONS,POST,DELETE\r\nContent-type: application/json\r\nContent-Length: 28\r\n\r\n{\"message\":\"Invalid method\"}";

    // conn-003.s2c @432: the answer to POST /ari/channels/{id}/mute?direction=both.
    private const string Mute204 =
        "HTTP/1.1 204 No Content\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:24 GMT\r\nCache-Control: no-cache, no-store\r\nContent-Length: 0\r\n\r\n";

    // conn-003.s2c @576: the answer to POST /ari/channels/{id}/mute with no direction.
    private const string MuteWithoutDirection400 =
        "HTTP/1.1 400 Bad Request\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:24 GMT\r\nCache-Control: no-cache, no-store\r\nContent-type: application/json\r\nContent-Length: 35\r\n\r\n{\"message\":\"Direction is required\"}";

    // conn-003.s2c @933: the answer to DELETE /ari/channels/{id}/mute with no direction.
    private const string UnmuteWithoutDirection400 =
        "HTTP/1.1 400 Bad Request\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:24 GMT\r\nCache-Control: no-cache, no-store\r\nContent-type: application/json\r\nContent-Length: 35\r\n\r\n{\"message\":\"Direction is required\"}";

    // conn-003.s2c @1146: the answer to DELETE /ari/channels/{id}/mute?direction=both.
    private const string Unmute204 =
        "HTTP/1.1 204 No Content\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:24 GMT\r\nCache-Control: no-cache, no-store\r\nContent-Length: 0\r\n\r\n";

    // conn-002.s2c @1462: the answer to POST /ari/asterisk/logging/messages.log/rotate.
    private const string Rotate405 =
        "HTTP/1.1 405 Method Not Allowed\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:24 GMT\r\nCache-Control: no-cache, no-store\r\nAllow: OPTIONS,PUT\r\nContent-type: application/json\r\nContent-Length: 28\r\n\r\n{\"message\":\"Invalid method\"}";

    // conn-003.s2c @789: the answer to PUT /ari/asterisk/logging/messages.log/rotate.
    private const string Rotate204 =
        "HTTP/1.1 204 No Content\r\nServer: Asterisk/22.11.0\r\nDate: Fri, 06 Nov 2026 00:14:24 GMT\r\nCache-Control: no-cache, no-store\r\nContent-Length: 0\r\n\r\n";

    private static readonly AriClientOptions DefaultOptions = new()
    {
        BaseUrl = "http://localhost:8088",
        Username = "admin",
        Password = "secret",
        Application = "testapp"
    };

    // The hold route as measured: POST holds (204), DELETE unholds (204), anything else is 405.
    private static string HoldRoute(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post || request.Method == HttpMethod.Delete ? Hold204 : Hold405;

    // The mute route as measured: POST mutes and DELETE unmutes, each 400 without a direction; anything else is 405.
    private static string MuteRoute(HttpRequestMessage request)
    {
        var hasDirection = QueryPairs(request.RequestUri!).Any(p => p.Key == "direction");
        if (request.Method == HttpMethod.Post) return hasDirection ? Mute204 : MuteWithoutDirection400;
        if (request.Method == HttpMethod.Delete) return hasDirection ? Unmute204 : UnmuteWithoutDirection400;
        return Mute405;
    }

    // The rotate route as measured: PUT rotates (204), anything else is 405.
    private static string RotateRoute(HttpRequestMessage request) =>
        request.Method == HttpMethod.Put ? Rotate204 : Rotate405;

    private static HttpClient CreateClient(CapturedAsteriskHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:8088/ari/") };

    [Fact]
    public async Task HoldAsync_ShouldComplete_WhenAsteriskAcceptsOnlyPost()
    {
        using var handler = new CapturedAsteriskHandler(HoldRoute);
        using var http = CreateClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.HoldAsync("1793924058.0");

        await act.Should().NotThrowAsync();
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task MuteAsync_ShouldComplete_WhenAsteriskAcceptsOnlyPost()
    {
        using var handler = new CapturedAsteriskHandler(MuteRoute);
        using var http = CreateClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.MuteAsync("1793924058.0", "both");

        await act.Should().NotThrowAsync();
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task MuteAsync_ShouldSendDirectionBoth_WhenDirectionIsNull()
    {
        using var handler = new CapturedAsteriskHandler(MuteRoute);
        using var http = CreateClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.MuteAsync("1793924058.0");

        await act.Should().NotThrowAsync();
        var request = handler.Requests.Should().ContainSingle().Subject;
        QueryPairs(request.Uri).Select(p => p.Key + "=" + p.Value).Should().Equal("direction=both");
    }

    [Fact]
    public async Task UnmuteAsync_ShouldSendDirectionBoth_WhenDirectionIsNull()
    {
        using var handler = new CapturedAsteriskHandler(MuteRoute);
        using var http = CreateClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.UnmuteAsync("1793924058.0");

        await act.Should().NotThrowAsync();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Delete);
        QueryPairs(request.Uri).Select(p => p.Key + "=" + p.Value).Should().Equal("direction=both");
    }

    [Fact]
    public async Task UnmuteAsync_ShouldSendOnlyTheCallersDirection_WhenDirectionIsGiven()
    {
        using var handler = new CapturedAsteriskHandler(MuteRoute);
        using var http = CreateClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        await sut.UnmuteAsync("1793924058.0", "in");

        var request = handler.Requests.Should().ContainSingle().Subject;
        QueryPairs(request.Uri).Select(p => p.Key + "=" + p.Value).Should().Equal("direction=in");
    }

    [Fact]
    public async Task RotateLogChannelAsync_ShouldComplete_WhenAsteriskAcceptsOnlyPut()
    {
        using var handler = new CapturedAsteriskHandler(RotateRoute);
        using var http = CreateClient(handler);
        var sut = new AriAsteriskResource(http);

        var act = async () => await sut.RotateLogChannelAsync("messages.log");

        await act.Should().NotThrowAsync();
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Put);
    }

    [Fact]
    public async Task HoldAsync_ShouldThrowAriExceptionWith405_WhenAsteriskRejectsTheVerb()
    {
        // An endpoint that refuses every verb on the hold route: the captured 405 must surface, never a success.
        using var handler = new CapturedAsteriskHandler(_ => Hold405);
        using var http = CreateClient(handler);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.HoldAsync("1793924058.0");

        var ex = await act.Should().ThrowAsync<AriException>();
        ex.Which.StatusCode.Should().Be(405);
        ex.Which.Message.Should().Contain("Invalid method");
    }

    private static List<KeyValuePair<string, string>> QueryPairs(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return [];
        return query.Split('&')
            .Select(pair =>
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                return eq < 0
                    ? new KeyValuePair<string, string>(Uri.UnescapeDataString(pair), "")
                    : new KeyValuePair<string, string>(
                        Uri.UnescapeDataString(pair[..eq]), Uri.UnescapeDataString(pair[(eq + 1)..]));
            })
            .ToList();
    }

    /// <summary>
    /// Answers each request with a raw HTTP/1.1 response captured from Asterisk — status line, headers and body as
    /// they crossed the wire — chosen by a route function from the request's verb and query.
    /// </summary>
    private sealed class CapturedAsteriskHandler(Func<HttpRequestMessage, string> route) : DelegatingHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!));
            return Task.FromResult(ParseRawResponse(route(request)));
        }

        private static HttpResponseMessage ParseRawResponse(string raw)
        {
            var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var lines = raw[..headerEnd].Split("\r\n");
            var statusCode = int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(raw[(headerEnd + 4)..]));
            var response = new HttpResponseMessage((HttpStatusCode)statusCode) { Content = content };
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var name = line[..colon];
                var value = line[(colon + 1)..].Trim();
                if (!response.Headers.TryAddWithoutValidation(name, value))
                    content.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        }
    }
}
