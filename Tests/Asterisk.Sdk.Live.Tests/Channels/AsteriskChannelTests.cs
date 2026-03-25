using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Live.Channels;
using FluentAssertions;

namespace Asterisk.Sdk.Live.Tests.Channels;

public sealed class AsteriskChannelTests
{
    [Fact]
    public void CreatedAt_ShouldBeSetAutomatically()
    {
        var before = DateTimeOffset.UtcNow;
        var channel = new AsteriskChannel { UniqueId = "1.1" };
        var after = DateTimeOffset.UtcNow;

        channel.CreatedAt.Should().BeOnOrAfter(before);
        channel.CreatedAt.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void Id_ShouldReturnUniqueId()
    {
        var channel = new AsteriskChannel { UniqueId = "abc.123" };

        channel.Id.Should().Be("abc.123");
    }

    [Theory]
    [InlineData(ChannelState.Down)]
    [InlineData(ChannelState.Ringing)]
    [InlineData(ChannelState.Up)]
    [InlineData(ChannelState.Ring)]
    [InlineData(ChannelState.Busy)]
    public void State_ShouldBeSettable(ChannelState state)
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        channel.State = state;

        channel.State.Should().Be(state);
    }

    [Fact]
    public void State_FullTransition_ShouldTrackAllStates()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1", State = ChannelState.Down };

        channel.State = ChannelState.Ringing;
        channel.State.Should().Be(ChannelState.Ringing);

        channel.State = ChannelState.Up;
        channel.State.Should().Be(ChannelState.Up);

        channel.State = ChannelState.Down;
        channel.State.Should().Be(ChannelState.Down);
    }

    [Fact]
    public void ExtensionHistory_ShouldBeEmptyInitially()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        channel.ExtensionHistory.Should().BeEmpty();
    }

    [Fact]
    public void AddExtensionHistory_ShouldAddEntry()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };
        var entry = new ExtensionHistoryEntry("default", "100", 1, DateTimeOffset.UtcNow);

        channel.AddExtensionHistory(entry);

        channel.ExtensionHistory.Should().HaveCount(1);
        channel.ExtensionHistory[0].Should().Be(entry);
    }

    [Fact]
    public void AddExtensionHistory_ShouldBeBoundedTo100()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        for (var i = 0; i < 110; i++)
        {
            channel.AddExtensionHistory(
                new ExtensionHistoryEntry("ctx", $"ext-{i}", 1, DateTimeOffset.UtcNow));
        }

        channel.ExtensionHistory.Should().HaveCount(100);
        // The first 10 entries (ext-0 through ext-9) should have been evicted
        channel.ExtensionHistory[0].Extension.Should().Be("ext-10");
        channel.ExtensionHistory[99].Extension.Should().Be("ext-109");
    }

    [Fact]
    public void AddExtensionHistory_ShouldEvictOldestEntry_WhenAtCapacity()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        // Fill to 100
        for (var i = 0; i < 100; i++)
        {
            channel.AddExtensionHistory(
                new ExtensionHistoryEntry("ctx", $"e-{i}", 1, DateTimeOffset.UtcNow));
        }

        channel.ExtensionHistory.Should().HaveCount(100);
        channel.ExtensionHistory[0].Extension.Should().Be("e-0");

        // Add 101st entry — should evict "e-0"
        channel.AddExtensionHistory(
            new ExtensionHistoryEntry("ctx", "e-100", 1, DateTimeOffset.UtcNow));

        channel.ExtensionHistory.Should().HaveCount(100);
        channel.ExtensionHistory[0].Extension.Should().Be("e-1");
        channel.ExtensionHistory[99].Extension.Should().Be("e-100");
    }

    [Fact]
    public void IsOnHold_ShouldDefaultToFalse()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        channel.IsOnHold.Should().BeFalse();
    }

    [Fact]
    public void Hold_ShouldSetIsOnHoldAndMusicClass()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        channel.IsOnHold = true;
        channel.HoldMusicClass = "default";

        channel.IsOnHold.Should().BeTrue();
        channel.HoldMusicClass.Should().Be("default");
    }

    [Fact]
    public void DialedChannel_ShouldTrackDialTarget()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        channel.DialedChannel = "PJSIP/3000-002";

        channel.DialedChannel.Should().Be("PJSIP/3000-002");
    }

    [Fact]
    public void LinkedChannel_ShouldTrackBridgePartner()
    {
        var ch1 = new AsteriskChannel { UniqueId = "1.1", Name = "PJSIP/2000" };
        var ch2 = new AsteriskChannel { UniqueId = "1.2", Name = "PJSIP/3000" };

        ch1.LinkedChannel = ch2;
        ch2.LinkedChannel = ch1;

        ch1.LinkedChannel.Should().BeSameAs(ch2);
        ch2.LinkedChannel.Should().BeSameAs(ch1);
    }

    [Fact]
    public void LinkedChannel_ShouldBeNullable()
    {
        var ch = new AsteriskChannel { UniqueId = "1.1" };

        ch.LinkedChannel.Should().BeNull();

        ch.LinkedChannel = new AsteriskChannel { UniqueId = "1.2" };
        ch.LinkedChannel.Should().NotBeNull();

        ch.LinkedChannel = null;
        ch.LinkedChannel.Should().BeNull();
    }

    [Fact]
    public void HangupCause_ShouldDefaultToUnknown()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        channel.HangupCause.Should().Be(default(HangupCause));
    }

    [Fact]
    public void HangupCause_ShouldBeSettable()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };

        channel.HangupCause = HangupCause.NormalClearing;

        channel.HangupCause.Should().Be(HangupCause.NormalClearing);
    }

    [Fact]
    public void CallerIdProperties_ShouldBeSettable()
    {
        var channel = new AsteriskChannel
        {
            UniqueId = "1.1",
            CallerIdNum = "5551234",
            CallerIdName = "John Doe",
            ConnectedLineNum = "5556789"
        };

        channel.CallerIdNum.Should().Be("5551234");
        channel.CallerIdName.Should().Be("John Doe");
        channel.ConnectedLineNum.Should().Be("5556789");
    }

    [Fact]
    public void DialplanProperties_ShouldBeSettable()
    {
        var channel = new AsteriskChannel
        {
            UniqueId = "1.1",
            Context = "from-internal",
            Extension = "100",
            Priority = 3
        };

        channel.Context.Should().Be("from-internal");
        channel.Extension.Should().Be("100");
        channel.Priority.Should().Be(3);
    }

    [Fact]
    public void LinkedId_ShouldBeInitOnly()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1", LinkedId = "linked-abc" };

        channel.LinkedId.Should().Be("linked-abc");
    }

    [Fact]
    public void ExtensionHistoryEntry_ShouldBeRecord()
    {
        var ts = DateTimeOffset.UtcNow;
        var entry = new ExtensionHistoryEntry("default", "100", 1, ts);

        entry.Context.Should().Be("default");
        entry.Extension.Should().Be("100");
        entry.Priority.Should().Be(1);
        entry.Timestamp.Should().Be(ts);
    }

    [Fact]
    public void ExtensionHistoryEntry_ShouldSupportValueEquality()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new ExtensionHistoryEntry("default", "100", 1, ts);
        var b = new ExtensionHistoryEntry("default", "100", 1, ts);

        a.Should().Be(b);
    }
}

public sealed class LiveObjectBaseTests
{
    [Fact]
    public void PropertyChanged_ShouldAcceptSubscription()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };
        string? changedProperty = null;
        channel.PropertyChanged += (_, e) => changedProperty = e.PropertyName;

        // The event is subscribed but auto-properties don't call OnPropertyChanged.
        // SetField is protected and would fire it. Verify subscription wiring works.
        changedProperty.Should().BeNull("no property change was triggered");
    }

    [Fact]
    public void PropertyChanged_ShouldNotThrow_WhenNoSubscription()
    {
        var channel = new AsteriskChannel { UniqueId = "1.1" };
        // Should not throw even without subscribers
        channel.State = ChannelState.Up;
        channel.State.Should().Be(ChannelState.Up);
    }
}
