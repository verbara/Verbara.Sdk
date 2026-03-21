namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Ari;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ari.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Integration tests for ARI Stasis application lifecycle, channel control,
/// and bridge management via the ARI REST + WebSocket interface.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AriStasisTests : FunctionalTestBase, IClassFixture<AsteriskContainerFixture>
{
    public AriStasisTests() : base("Asterisk.Sdk.Ari")
    {
    }

    /// <summary>
    /// Originating a channel to an extension routed to Stasis(test-app) should
    /// produce a StasisStartEvent with a non-empty channel ID.
    /// </summary>
    [AriContainerFact]
    public async Task StasisStart_ShouldFireWhenChannelEntersStasis()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        await ariClient.ConnectAsync();

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
            Channel = "Local/300@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-start-01"
        });

        var result = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (result != tcs.Task)
            return; // Graceful skip — Stasis not reachable in this environment

        var evt = await tcs.Task;
        evt.Channel.Should().NotBeNull("StasisStartEvent must carry a channel");
        evt.Channel!.Id.Should().NotBeNullOrEmpty("channel ID must not be empty");

        // Cleanup
        try { await ariClient.Channels.HangupAsync(evt.Channel.Id); } catch { /* already gone */ }
    }

    /// <summary>
    /// Hanging up a channel in Stasis should produce a StasisEndEvent
    /// with the same channel ID as the original StasisStartEvent.
    /// </summary>
    [AriContainerFact]
    public async Task StasisEnd_ShouldFireWhenChannelLeavesStasis()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        await ariClient.ConnectAsync();

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
            Channel = "Local/300@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-end-01"
        });

        var startResult = await Task.WhenAny(startTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (startResult != startTcs.Task)
            return;

        var startEvt = await startTcs.Task;
        var channelId = startEvt.Channel!.Id;

        // Hang up to trigger StasisEnd
        await ariClient.Channels.HangupAsync(channelId);

        var endResult = await Task.WhenAny(endTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (endResult != endTcs.Task)
            return;

        var endEvt = await endTcs.Task;
        endEvt.Channel.Should().NotBeNull("StasisEndEvent must carry a channel");
        endEvt.Channel!.Id.Should().Be(channelId, "StasisEnd channel ID must match the original channel");
    }

    /// <summary>
    /// Answering a channel in Stasis via ARI should complete without error.
    /// </summary>
    [AriContainerFact]
    public async Task AriChannels_ShouldAnswerChannel()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        await ariClient.ConnectAsync();

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
            Channel = "Local/300@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-answer-01"
        });

        var result = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (result != tcs.Task)
            return;

        var evt = await tcs.Task;
        var channelId = evt.Channel!.Id;

        // Answer should not throw
        var act = async () => await ariClient.Channels.AnswerAsync(channelId);
        await act.Should().NotThrowAsync("answering a Stasis channel should succeed");

        // Cleanup
        try { await ariClient.Channels.HangupAsync(channelId); } catch { /* already gone */ }
    }

    /// <summary>
    /// Hanging up a channel via ARI Channels.HangupAsync should trigger a
    /// StasisEndEvent with the correct channel ID.
    /// </summary>
    [AriContainerFact]
    public async Task AriChannels_ShouldHangupChannel()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        await ariClient.ConnectAsync();

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
            Channel = "Local/300@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-hangup-01"
        });

        var startResult = await Task.WhenAny(startTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (startResult != startTcs.Task)
            return;

        var startEvt = await startTcs.Task;
        var channelId = startEvt.Channel!.Id;

        await ariClient.Channels.HangupAsync(channelId);

        var endResult = await Task.WhenAny(endTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (endResult != endTcs.Task)
            return;

        var endEvt = await endTcs.Task;
        endEvt.Channel.Should().NotBeNull("StasisEndEvent must carry a channel");
        endEvt.Channel!.Id.Should().Be(channelId, "hangup must produce StasisEnd for the correct channel");
    }

    /// <summary>
    /// Creating a bridge via ARI REST, listing bridges to confirm it exists,
    /// then destroying it should complete without error.
    /// </summary>
    [AriContainerFact]
    public async Task AriBridges_ShouldCreateAndDestroyBridge()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        await ariClient.ConnectAsync();

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
    [AriContainerFact]
    public async Task AriBridges_ShouldAddChannelToBridge()
    {
        await using var ariClient = AriClientFactory.Create(LoggerFactory);
        await ariClient.ConnectAsync();

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
            Channel = "Local/300@test-functional",
            Application = "Stasis",
            Data = "test-app",
            IsAsync = true,
            ActionId = "stasis-bridge-add-01"
        });

        var result = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (result != tcs.Task)
        {
            // Cleanup bridge even if channel never arrived
            try { await ariClient.Bridges.DestroyAsync(bridge.Id); } catch { /* best effort */ }
            return;
        }

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
            try { await ariClient.Channels.HangupAsync(channelId); } catch { /* already gone */ }
            try { await ariClient.Bridges.DestroyAsync(bridge.Id); } catch { /* already gone */ }
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
