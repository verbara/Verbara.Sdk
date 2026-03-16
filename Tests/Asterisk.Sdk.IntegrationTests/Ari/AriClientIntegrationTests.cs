using Asterisk.Sdk;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Asterisk.Sdk.IntegrationTests.Ari;

[Trait("Category", "Integration")]
public class AriClientIntegrationTests : IClassFixture<AsteriskFixture>, IAsyncLifetime
{
    private readonly AsteriskFixture _fixture;
    private Asterisk.Sdk.Ari.Client.AriClient? _client;

    public AriClientIntegrationTests(AsteriskFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _client = _fixture.CreateAriClient();
        await _client.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    [AsteriskAvailableFact]
    public void Connect_ShouldSetIsConnectedToTrue()
    {
        _client!.IsConnected.Should().BeTrue();
    }

    [AsteriskAvailableFact]
    public async Task CreateChannel_ShouldReturnChannel()
    {
        var channel = await _client!.Channels.CreateAsync("Local/100@default", _fixture.AriApp);
        channel.Should().NotBeNull();
        channel.Id.Should().NotBeNullOrEmpty();

        // Clean up
        try { await _client.Channels.HangupAsync(channel.Id); } catch { /* best effort */ }
    }

    [AsteriskAvailableFact]
    public async Task CreateBridge_ShouldReturnBridge()
    {
        var bridge = await _client!.Bridges.CreateAsync("mixing", "test-bridge");
        bridge.Should().NotBeNull();
        bridge.Id.Should().NotBeNullOrEmpty();

        // Clean up
        try { await _client.Bridges.DestroyAsync(bridge.Id); } catch { /* best effort */ }
    }

    [AsteriskAvailableFact]
    public async Task Subscribe_ShouldReceiveEvents()
    {
        var eventReceived = new TaskCompletionSource<AriEvent>();
        using var sub = _client!.Subscribe(new TestObserver(eventReceived));

        // Create a channel to trigger StasisStart
        var channel = await _client.Channels.CreateAsync("Local/300@default", _fixture.AriApp);

        try
        {
            var evt = await eventReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            evt.Should().NotBeNull();
            evt.Type.Should().NotBeNullOrEmpty();
        }
        finally
        {
            try { await _client.Channels.HangupAsync(channel.Id); } catch { /* best effort */ }
        }
    }

    [AsteriskAvailableFact]
    public async Task Disconnect_ShouldSetIsConnectedToFalse()
    {
        await _client!.DisconnectAsync();
        _client.IsConnected.Should().BeFalse();
        _client = null;
    }

    [AsteriskAvailableFact]
    public async Task CreateBridgeAndAddChannel_ShouldWork()
    {
        var bridge = await _client!.Bridges.CreateAsync("mixing", "test-bridge-add");
        var channel = await _client.Channels.CreateAsync("Local/100@default", _fixture.AriApp);

        try
        {
            await _client.Bridges.AddChannelAsync(bridge.Id, channel.Id);

            var updatedBridge = await _client.Bridges.GetAsync(bridge.Id);
            updatedBridge.Channels.Should().Contain(channel.Id);
        }
        finally
        {
            try { await _client.Channels.HangupAsync(channel.Id); } catch { /* best effort */ }
            try { await _client.Bridges.DestroyAsync(bridge.Id); } catch { /* best effort */ }
        }
    }

    private sealed class TestObserver(TaskCompletionSource<AriEvent> tcs) : IObserver<AriEvent>
    {
        public void OnNext(AriEvent value) => tcs.TrySetResult(value);
        public void OnError(Exception error) => tcs.TrySetException(error);
        public void OnCompleted() { }
    }
}
