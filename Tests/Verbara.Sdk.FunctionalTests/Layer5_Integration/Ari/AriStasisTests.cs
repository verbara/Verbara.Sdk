namespace Verbara.Sdk.FunctionalTests.Layer5_Integration.Ari;

using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ari.Events;
using Verbara.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Verbara.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Integration tests for ARI Stasis application lifecycle, channel control,
/// and bridge management via the ARI REST + WebSocket interface.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AriStasisTests : FunctionalTestBase
{
    private const string StasisStartReason =
        "a channel originated with Application=Stasis, Data=test-app must enter Stasis and produce a StasisStart within 10 s";

    public AriStasisTests() : base("Verbara.Sdk.Ari")
    {
    }

    /// <summary>
    /// Originating a channel to an extension routed to Stasis(test-app) should
    /// produce a StasisStartEvent with a non-empty channel ID.
    /// </summary>
    [Fact]
    public async Task StasisStart_ShouldFireWhenChannelEntersStasis()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        var connect = async () => await ariClient.ConnectAsync();
        await connect.Should().NotThrowAsync(
            "the functional fixture runs Asterisk with ARI enabled for user testari, so the event WebSocket for test-app must open");

        var tcs = new TaskCompletionSource<StasisStartEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = ariClient.Subscribe(new AriEventObserver(
            onStasisStart: e => tcs.TrySetResult(e)));

        await using var amiConnection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await amiConnection.ConnectAsync();

        await amiConnection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-start-01"
        });

        var result = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        result.Should().Be(tcs.Task, StasisStartReason);

        var evt = await tcs.Task;
        evt.Channel.Should().NotBeNull("StasisStartEvent must carry a channel");
        evt.Channel!.Id.Should().NotBeNullOrEmpty("channel ID must not be empty");

        // Cleanup
        await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(evt.Channel.Id));
    }

    /// <summary>
    /// Hanging up a channel in Stasis should produce a StasisEndEvent
    /// with the same channel ID as the original StasisStartEvent.
    /// </summary>
    [Fact]
    public async Task StasisEnd_ShouldFireWhenChannelLeavesStasis()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        var connect = async () => await ariClient.ConnectAsync();
        await connect.Should().NotThrowAsync(
            "the functional fixture runs Asterisk with ARI enabled for user testari, so the event WebSocket for test-app must open");

        var startTcs = new TaskCompletionSource<StasisStartEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var endTcs = new TaskCompletionSource<StasisEndEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = ariClient.Subscribe(new AriEventObserver(
            onStasisStart: e => startTcs.TrySetResult(e),
            onStasisEnd: e => endTcs.TrySetResult(e)));

        await using var amiConnection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await amiConnection.ConnectAsync();

        await amiConnection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-end-01"
        });

        var startResult = await Task.WhenAny(startTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        startResult.Should().Be(startTcs.Task, StasisStartReason);

        var startEvt = await startTcs.Task;
        var channelId = startEvt.Channel!.Id;

        // Hang up to trigger StasisEnd
        await ariClient.Channels.HangupAsync(channelId);

        var endResult = await Task.WhenAny(endTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        endResult.Should().Be(endTcs.Task, "hanging up a channel in Stasis must produce a StasisEnd within 10 s");

        var endEvt = await endTcs.Task;
        endEvt.Channel.Should().NotBeNull("StasisEndEvent must carry a channel");
        endEvt.Channel!.Id.Should().Be(channelId, "StasisEnd channel ID must match the original channel");
    }

    /// <summary>
    /// Answering a channel in Stasis via ARI should complete without error.
    /// </summary>
    [Fact]
    public async Task AriChannels_ShouldAnswerChannel()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        var connect = async () => await ariClient.ConnectAsync();
        await connect.Should().NotThrowAsync(
            "the functional fixture runs Asterisk with ARI enabled for user testari, so the event WebSocket for test-app must open");

        var tcs = new TaskCompletionSource<StasisStartEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = ariClient.Subscribe(new AriEventObserver(
            onStasisStart: e => tcs.TrySetResult(e)));

        await using var amiConnection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await amiConnection.ConnectAsync();

        await amiConnection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-answer-01"
        });

        var result = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        result.Should().Be(tcs.Task, StasisStartReason);

        var evt = await tcs.Task;
        var channelId = evt.Channel!.Id;

        // Answer should not throw
        var act = async () => await ariClient.Channels.AnswerAsync(channelId);
        await act.Should().NotThrowAsync("answering a Stasis channel should succeed");

        // Cleanup
        await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(channelId));
    }

    /// <summary>
    /// Hanging up a channel via ARI Channels.HangupAsync should trigger a
    /// StasisEndEvent with the correct channel ID.
    /// </summary>
    [Fact]
    public async Task AriChannels_ShouldHangupChannel()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        var connect = async () => await ariClient.ConnectAsync();
        await connect.Should().NotThrowAsync(
            "the functional fixture runs Asterisk with ARI enabled for user testari, so the event WebSocket for test-app must open");

        var startTcs = new TaskCompletionSource<StasisStartEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var endTcs = new TaskCompletionSource<StasisEndEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = ariClient.Subscribe(new AriEventObserver(
            onStasisStart: e => startTcs.TrySetResult(e),
            onStasisEnd: e => endTcs.TrySetResult(e)));

        await using var amiConnection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await amiConnection.ConnectAsync();

        await amiConnection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-hangup-01"
        });

        var startResult = await Task.WhenAny(startTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        startResult.Should().Be(startTcs.Task, StasisStartReason);

        var startEvt = await startTcs.Task;
        var channelId = startEvt.Channel!.Id;

        await ariClient.Channels.HangupAsync(channelId);

        var endResult = await Task.WhenAny(endTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        endResult.Should().Be(endTcs.Task, "hanging up a channel in Stasis must produce a StasisEnd within 10 s");

        var endEvt = await endTcs.Task;
        endEvt.Channel.Should().NotBeNull("StasisEndEvent must carry a channel");
        endEvt.Channel!.Id.Should().Be(channelId, "hangup must produce StasisEnd for the correct channel");
    }

    /// <summary>
    /// Creating a bridge via ARI REST, listing bridges to confirm it exists,
    /// then destroying it should complete without error.
    /// </summary>
    [Fact]
    public async Task AriBridges_ShouldCreateAndDestroyBridge()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        var connect = async () => await ariClient.ConnectAsync();
        await connect.Should().NotThrowAsync(
            "the functional fixture runs Asterisk with ARI enabled for user testari, so the event WebSocket for test-app must open");

        var bridge = await ariClient.Bridges.CreateAsync(type: "mixing", name: "test-bridge-ari-01");
        bridge.Should().NotBeNull("CreateAsync must return a bridge");
        bridge.Id.Should().NotBeNullOrEmpty("bridge must have an ID");

        // List bridges and verify ours is present
        var bridges = await ariClient.Bridges.ListAsync();
        bridges.Should().Contain(b => b.Id == bridge.Id, "newly created bridge must appear in the list");

        // Destroy and verify no exception
        var act = async () => await ariClient.Bridges.DestroyAsync(bridge.Id);
        await act.Should().NotThrowAsync("destroying an existing bridge should succeed");
    }

    /// <summary>
    /// Adding a Stasis channel to a bridge should result in the bridge's
    /// Channels collection containing the channel ID.
    /// </summary>
    [Fact]
    public async Task AriBridges_ShouldAddChannelToBridge()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        var connect = async () => await ariClient.ConnectAsync();
        await connect.Should().NotThrowAsync(
            "the functional fixture runs Asterisk with ARI enabled for user testari, so the event WebSocket for test-app must open");

        var tcs = new TaskCompletionSource<StasisStartEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = ariClient.Subscribe(new AriEventObserver(
            onStasisStart: e => tcs.TrySetResult(e)));

        await using var amiConnection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await amiConnection.ConnectAsync();

        // Create a bridge first
        var bridge = await ariClient.Bridges.CreateAsync(type: "mixing", name: "test-bridge-ari-02");
        bridge.Should().NotBeNull("CreateAsync must return a bridge");

        // Originate a channel into Stasis
        await amiConnection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-bridge-add-01"
        });

        var result = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (result != tcs.Task)
        {
            // Cleanup bridge even if channel never arrived
            await BestEffort.AriAsync(() => ariClient.Bridges.DestroyAsync(bridge.Id));
        }

        result.Should().Be(tcs.Task, StasisStartReason);

        var evt = await tcs.Task;
        var channelId = evt.Channel!.Id;

        try
        {
            // Answer the channel before adding to bridge
            await ariClient.Channels.AnswerAsync(channelId);

            // Add channel to bridge
            await ariClient.Bridges.AddChannelAsync(bridge.Id, channelId);

            // Allow a brief moment for the bridge state to update
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Verify bridge contains the channel
            var bridges = await ariClient.Bridges.ListAsync();
            var updatedBridge = bridges.FirstOrDefault(b => b.Id == bridge.Id);
            updatedBridge.Should().NotBeNull("bridge must still exist after adding a channel");
            updatedBridge!.Channels.Should().Contain(channelId,
                "bridge Channels list must include the added channel ID");
        }
        finally
        {
            await BestEffort.AriAsync(() => ariClient.Channels.HangupAsync(channelId));
            await BestEffort.AriAsync(() => ariClient.Bridges.DestroyAsync(bridge.Id));
        }
    }

    /// <summary>Observer that routes ARI events to typed callbacks.</summary>
    private sealed class AriEventObserver(
        Action<StasisStartEvent>? onStasisStart = null,
        Action<StasisEndEvent>? onStasisEnd = null) : IObserver<AriEvent>
    {
        public void OnNext(AriEvent value)
        {
            switch (value)
            {
                case StasisStartEvent e: onStasisStart?.Invoke(e); break;
                case StasisEndEvent e: onStasisEnd?.Invoke(e); break;
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
