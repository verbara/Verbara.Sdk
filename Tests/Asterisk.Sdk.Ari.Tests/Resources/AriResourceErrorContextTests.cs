using System.Net;
using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Ari.Resources;
using FluentAssertions;

namespace Asterisk.Sdk.Ari.Tests.Resources;

/// <summary>
/// Validates that REST resources surface 404/409 responses as <see cref="AriNotFoundException"/>
/// and <see cref="AriConflictException"/> with enriched message context (resource name + id).
/// Covers task B1 of the SDK v1.6.0 Sprint 1 plan.
/// </summary>
public sealed class AriResourceErrorContextTests
{
    private static readonly AriClientOptions DefaultOptions = new()
    {
        BaseUrl = "http://localhost:8088",
        Username = "admin",
        Password = "secret",
        Application = "testapp"
    };

    private static HttpClient CreateClient(HttpStatusCode status, string body = "")
    {
        var handler = new FakeHttpHandler(body, status);
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8088/ari/") };
    }

    // --- Channels ---

    [Fact]
    public async Task Channels_GetAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.GetAsync("SIP/1234-00000001");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Channel").And.Contain("SIP/1234-00000001");
        ex.Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Channels_HoldAsync_ShouldThrowAriConflictException_When409()
    {
        using var http = CreateClient(HttpStatusCode.Conflict, "channel not in valid state");
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.HoldAsync("ch-99");

        var ex = await act.Should().ThrowAsync<AriConflictException>();
        ex.Which.Message.Should().Contain("Channel").And.Contain("ch-99");
        ex.Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task Channels_HangupAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.HangupAsync("ch-missing");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("ch-missing");
    }

    // --- Bridges ---

    [Fact]
    public async Task Bridges_GetAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var act = async () => await sut.GetAsync("br-1");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Bridge").And.Contain("br-1");
    }

    [Fact]
    public async Task Bridges_AddChannelAsync_ShouldThrowAriConflictException_When409()
    {
        using var http = CreateClient(HttpStatusCode.Conflict);
        var sut = new AriBridgesResource(http, DefaultOptions);

        var act = async () => await sut.AddChannelAsync("br-1", "ch-1");

        var ex = await act.Should().ThrowAsync<AriConflictException>();
        ex.Which.Message.Should().Contain("Bridge").And.Contain("br-1");
    }

    // --- Recordings ---

    [Fact]
    public async Task Recordings_GetLiveAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriRecordingsResource(http);

        var act = async () => await sut.GetLiveAsync("rec-1");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Recording").And.Contain("rec-1");
    }

    [Fact]
    public async Task Recordings_PauseAsync_ShouldThrowAriConflictException_When409()
    {
        using var http = CreateClient(HttpStatusCode.Conflict);
        var sut = new AriRecordingsResource(http);

        var act = async () => await sut.PauseAsync("rec-2");

        var ex = await act.Should().ThrowAsync<AriConflictException>();
        ex.Which.Message.Should().Contain("rec-2");
    }

    // --- Applications ---

    [Fact]
    public async Task Applications_GetAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriApplicationsResource(http);

        var act = async () => await sut.GetAsync("my-app");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Application").And.Contain("my-app");
    }

    [Fact]
    public async Task Applications_SubscribeAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriApplicationsResource(http);

        var act = async () => await sut.SubscribeAsync("my-app", "channel:42");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("my-app");
    }

    // --- Endpoints ---

    [Fact]
    public async Task Endpoints_GetAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriEndpointsResource(http);

        var act = async () => await sut.GetAsync("PJSIP", "1000");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Endpoint").And.Contain("PJSIP/1000");
    }

    // --- DeviceStates ---

    [Fact]
    public async Task DeviceStates_GetAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriDeviceStatesResource(http);

        var act = async () => await sut.GetAsync("Custom:foo");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("DeviceState").And.Contain("Custom:foo");
    }

    [Fact]
    public async Task DeviceStates_DeleteAsync_ShouldThrowAriConflictException_When409()
    {
        using var http = CreateClient(HttpStatusCode.Conflict);
        var sut = new AriDeviceStatesResource(http);

        var act = async () => await sut.DeleteAsync("Custom:foo");

        var ex = await act.Should().ThrowAsync<AriConflictException>();
        ex.Which.Message.Should().Contain("Custom:foo");
    }

    // --- Mailboxes ---

    [Fact]
    public async Task Mailboxes_GetAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriMailboxesResource(http);

        var act = async () => await sut.GetAsync("vm-100");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Mailbox").And.Contain("vm-100");
    }

    [Fact]
    public async Task Mailboxes_DeleteAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriMailboxesResource(http);

        var act = async () => await sut.DeleteAsync("vm-404");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("vm-404");
    }

    // --- Playbacks ---

    [Fact]
    public async Task Playbacks_GetAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriPlaybacksResource(http);

        var act = async () => await sut.GetAsync("pb-1");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Playback").And.Contain("pb-1");
    }

    [Fact]
    public async Task Playbacks_ControlAsync_ShouldThrowAriConflictException_When409()
    {
        using var http = CreateClient(HttpStatusCode.Conflict);
        var sut = new AriPlaybacksResource(http);

        var act = async () => await sut.ControlAsync("pb-2", "pause");

        var ex = await act.Should().ThrowAsync<AriConflictException>();
        ex.Which.Message.Should().Contain("pb-2");
    }

    // --- Sounds ---

    [Fact]
    public async Task Sounds_GetAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriSoundsResource(http);

        var act = async () => await sut.GetAsync("hello-world");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Sound").And.Contain("hello-world");
    }

    // --- Asterisk ---

    [Fact]
    public async Task Asterisk_GetModuleAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriAsteriskResource(http);

        var act = async () => await sut.GetModuleAsync("res_pjsip.so");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Module").And.Contain("res_pjsip.so");
    }

    [Fact]
    public async Task Asterisk_LoadModuleAsync_ShouldThrowAriConflictException_When409()
    {
        using var http = CreateClient(HttpStatusCode.Conflict, "already loaded");
        var sut = new AriAsteriskResource(http);

        var act = async () => await sut.LoadModuleAsync("res_pjsip.so");

        var ex = await act.Should().ThrowAsync<AriConflictException>();
        ex.Which.Message.Should().Contain("res_pjsip.so");
    }

    [Fact]
    public async Task Asterisk_GetConfigAsync_ShouldThrowAriNotFoundException_When404()
    {
        using var http = CreateClient(HttpStatusCode.NotFound);
        var sut = new AriAsteriskResource(http);

        var act = async () => await sut.GetConfigAsync("res_pjsip", "endpoint", "ep-1");

        var ex = await act.Should().ThrowAsync<AriNotFoundException>();
        ex.Which.Message.Should().Contain("Config").And.Contain("ep-1");
    }

    // --- Generic 500 still wraps as AriException without resource context noise ---

    [Fact]
    public async Task Channels_GetAsync_ShouldThrowAriException_When500()
    {
        using var http = CreateClient(HttpStatusCode.InternalServerError, "server boom");
        var sut = new AriChannelsResource(http, DefaultOptions);

        var act = async () => await sut.GetAsync("ch-x");

        var ex = await act.Should().ThrowAsync<AriException>();
        ex.Which.StatusCode.Should().Be(500);
        ex.Which.Message.Should().Contain("Channel").And.Contain("ch-x").And.Contain("500");
    }
}
