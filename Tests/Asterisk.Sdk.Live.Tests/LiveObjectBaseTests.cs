using System.ComponentModel;
using Asterisk.Sdk.Live.Channels;
using FluentAssertions;

namespace Asterisk.Sdk.Live.Tests;

public sealed class LiveObjectBaseTests
{
    [Fact]
    public void AsteriskChannel_ShouldImplementILiveObject()
    {
        var channel = new AsteriskChannel { UniqueId = "test-123" };

        channel.Id.Should().Be("test-123");
        channel.Should().BeAssignableTo<ILiveObject>();
    }

    [Fact]
    public void AsteriskChannel_PropertyChanged_ShouldNotFire_WhenNoSubscriber()
    {
        // Should not throw
        var channel = new AsteriskChannel { UniqueId = "test-123" };
        var act = () => channel.State = Asterisk.Sdk.Enums.ChannelState.Up;
        act.Should().NotThrow();
    }

    [Fact]
    public void ExtensionHistory_ShouldBeBoundedTo100Entries()
    {
        var channel = new AsteriskChannel { UniqueId = "test-1" };

        for (int i = 0; i < 110; i++)
        {
            channel.AddExtensionHistory(new ExtensionHistoryEntry(
                "ctx", $"ext-{i}", 1, DateTimeOffset.UtcNow));
        }

        channel.ExtensionHistory.Should().HaveCount(100);
        // First 10 entries should have been removed
        channel.ExtensionHistory[0].Extension.Should().Be("ext-10");
        channel.ExtensionHistory[99].Extension.Should().Be("ext-109");
    }

    [Fact]
    public void ExtensionHistory_ShouldBeEmpty_Initially()
    {
        var channel = new AsteriskChannel { UniqueId = "test-1" };

        channel.ExtensionHistory.Should().BeEmpty();
    }
}
