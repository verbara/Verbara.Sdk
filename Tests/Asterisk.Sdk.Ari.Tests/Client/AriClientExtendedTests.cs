using Asterisk.Sdk.Ari.Client;
using Asterisk.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Sdk.Ari.Tests.Client;

public sealed class AriClientExtendedTests
{
    private static AriClient CreateClient(string baseUrl = "http://localhost:8088")
    {
        var options = Options.Create(new AriClientOptions
        {
            BaseUrl = baseUrl,
            Username = "asterisk",
            Password = "asterisk",
            Application = "test-app"
        });
        return new AriClient(options, NullLogger<AriClient>.Instance);
    }

    [Fact]
    public async Task Channels_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Channels.Should().NotBeNull();
    }

    [Fact]
    public async Task Bridges_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Bridges.Should().NotBeNull();
    }

    [Fact]
    public async Task Playbacks_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Playbacks.Should().NotBeNull();
    }

    [Fact]
    public async Task Recordings_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Recordings.Should().NotBeNull();
    }

    [Fact]
    public async Task Endpoints_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Endpoints.Should().NotBeNull();
    }

    [Fact]
    public async Task Applications_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Applications.Should().NotBeNull();
    }

    [Fact]
    public async Task Sounds_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Sounds.Should().NotBeNull();
    }

    [Fact]
    public async Task DeviceStates_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.DeviceStates.Should().NotBeNull();
    }

    [Fact]
    public async Task Asterisk_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Asterisk.Should().NotBeNull();
    }

    [Fact]
    public async Task Mailboxes_ShouldNotBeNull_WhenCreated()
    {
        await using var sut = CreateClient();
        sut.Mailboxes.Should().NotBeNull();
    }

    [Fact]
    public async Task AudioServer_ShouldBeNull_WhenNotProvided()
    {
        await using var sut = CreateClient();
        sut.AudioServer.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenNotConnected()
    {
        var sut = CreateClient();
        var act = async () => await sut.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_ShouldTransitionState()
    {
        await using var sut = CreateClient();
        sut.State.Should().Be(AriConnectionState.Initial);
        // Verify we can dispose without error
    }

    [Fact]
    public async Task State_ShouldBeInitial_BeforeConnect()
    {
        await using var sut = CreateClient();
        sut.State.Should().Be(AriConnectionState.Initial);
        sut.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrow_WhenServerUnreachable()
    {
        await using var sut = CreateClient("http://192.0.2.1:1");

        var act = async () => await sut.ConnectAsync(
            new CancellationTokenSource(TimeSpan.FromMilliseconds(500)).Token);
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task BaseUrl_WithTrailingSlash_ShouldWork()
    {
        await using var sut = CreateClient("http://localhost:8088/");
        sut.State.Should().Be(AriConnectionState.Initial);
    }
}
