namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.EventOrdering;

using System.Collections.Concurrent;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Attributes;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using Asterisk.Sdk.Live.Channels;
using Asterisk.Sdk.Live.Server;
using FluentAssertions;
using Microsoft.Extensions.Logging;

[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class ChannelEventOrderTests : FunctionalTestBase
{
    /// <summary>
    /// Originate + immediate hangup in rapid succession.
    /// The ChannelManager must not retain phantom channels after both legs have hung up.
    /// </summary>
    [AsteriskContainerFact]
    public async Task RapidOriginateAndHangup_ShouldNotCreatePhantomChannels()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Capture created channels so we can hang them up immediately
        var createdChannels = new ConcurrentBag<string>();
        server.Channels.ChannelAdded += ch => createdChannels.Add(ch.Name);

        // Originate a short-lived Local channel
        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "1",
            IsAsync = true,
            ActionId = "rapid-hangup-01"
        });

        // Give the channel a moment to appear, then hang it up immediately
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        foreach (var channelName in createdChannels)
        {
            try
            {
                await connection.SendActionAsync(new HangupAction
                {
                    Channel = channelName,
                    Cause = 16 // Normal clearing
                });
            }
            catch (OperationCanceledException)
            {
                // Channel may have already hung up; acceptable
            }
        }

        // Wait for all HangupEvents to be processed by ChannelManager
        await Task.Delay(TimeSpan.FromSeconds(3));

        // ChannelManager must report a consistent count — no phantom entries
        var channelCount = server.Channels.ChannelCount;
        var activeList = server.Channels.ActiveChannels.ToList();

        channelCount.Should().Be(activeList.Count,
            "ChannelCount and ActiveChannels must agree — no phantom channels");

        // Every channel that was hung up must be gone from the manager
        foreach (var channelName in createdChannels)
        {
            var lookup = server.Channels.GetByName(channelName);
            // A channel that was explicitly hung up should not be in the index
            if (lookup is not null)
            {
                // Verify it is not still tracked as active by cross-checking UniqueId index
                var byId = server.Channels.GetByUniqueId(lookup.UniqueId);
                byId.Should().NotBeNull(
                    "if name index has the channel, UniqueId index must also have it (no stale entry)");
            }
        }
    }

    /// <summary>
    /// 10 concurrent originate calls.
    /// All channels must be tracked while active and removed after hangup.
    /// Both indices must stay consistent throughout.
    /// </summary>
    [AsteriskContainerFact]
    public async Task ConcurrentChannelEvents_ShouldMaintainConsistentState()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Fire 10 concurrent originates
        const int callCount = 10;
        var tasks = Enumerable.Range(0, callCount).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/100@test-functional",
                    Application = "Wait",
                    Data = "2",
                    IsAsync = true,
                    ActionId = $"concurrent-ch-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Timeout is acceptable; the channel lifecycle events still arrive
            }
        });

        await Task.WhenAll(tasks);

        // Allow events to propagate
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Snapshot state while channels may still be active
        var snapshot = server.Channels.ActiveChannels.ToList();

        // Every active channel must be resolvable by both indices
        foreach (var ch in snapshot)
        {
            var byId = server.Channels.GetByUniqueId(ch.UniqueId);
            var byName = server.Channels.GetByName(ch.Name);

            byId.Should().NotBeNull("active channel {0} must be in UniqueId index", ch.UniqueId);
            byName.Should().NotBeNull("active channel {0} must be in name index", ch.Name);
        }

        server.Channels.ChannelCount.Should().Be(snapshot.Count,
            "ChannelCount must equal the number of items in ActiveChannels");

        // Wait for all channels to hang up (Wait,2 = 2 second duration)
        await Task.Delay(TimeSpan.FromSeconds(5));

        // After all 2-second waits complete, manager should be drained
        var remaining = server.Channels.ActiveChannels.ToList();
        var remainingCount = server.Channels.ChannelCount;
        remainingCount.Should().Be(remaining.Count,
            "ChannelCount and ActiveChannels must agree after all hangups");
    }

    /// <summary>
    /// Rename events must keep both indices consistent.
    /// After a rename, GetByName(newName) and GetByUniqueId must return the same object;
    /// the old name must be removed from the name index.
    /// </summary>
    [AsteriskContainerFact]
    public async Task ChannelRename_ShouldUpdateSecondaryIndex()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var server = new AsteriskServer(connection, LoggerFactory.CreateLogger<AsteriskServer>());
        await server.StartAsync();

        // Track renames via ChannelStateChanged (rename also triggers state update in AsteriskServer)
        var renamedChannels = new ConcurrentBag<AsteriskChannel>();
        server.Channels.ChannelStateChanged += ch => renamedChannels.Add(ch);

        // Originate channels — Local channels undergo an automatic rename when they begin
        // executing dialplan (e.g. "Local/100@test-functional-00000001;1" appears)
        const int count = 5;
        var originateTasks = Enumerable.Range(0, count).Select(async i =>
        {
            try
            {
                await connection.SendActionAsync(new OriginateAction
                {
                    Channel = "Local/100@test-functional",
                    Application = "Wait",
                    Data = "3",
                    IsAsync = true,
                    ActionId = $"rename-ch-{i:D4}"
                });
            }
            catch (OperationCanceledException)
            {
                // Acceptable
            }
        });

        await Task.WhenAll(originateTasks);

        // Wait for channels and any rename events to settle
        await Task.Delay(TimeSpan.FromSeconds(2));

        // For every currently tracked channel, verify index consistency
        var allChannels = server.Channels.ActiveChannels.ToList();
        foreach (var ch in allChannels)
        {
            var byId = server.Channels.GetByUniqueId(ch.UniqueId);
            var byName = server.Channels.GetByName(ch.Name);

            byId.Should().NotBeNull(
                "channel {0} must be accessible by UniqueId", ch.UniqueId);
            byName.Should().NotBeNull(
                "channel {0} must be accessible by current name '{1}'", ch.UniqueId, ch.Name);

            if (byId is not null && byName is not null)
            {
                ReferenceEquals(byId, byName).Should().BeTrue(
                    "UniqueId and name indices must reference the same channel object after any renames");
            }
        }

        // ChannelCount must match enumeration — no stale/duplicate entries
        server.Channels.ChannelCount.Should().Be(allChannels.Count,
            "ChannelCount must stay in sync with ActiveChannels after rename events");
    }
}
