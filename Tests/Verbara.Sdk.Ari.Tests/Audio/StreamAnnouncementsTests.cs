using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Diagnostics;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The guard behind each server's <c>OnStreamConnected</c>, on its own: what it does not catch, and what it refuses.
/// What it catches is measured on the real servers in <see cref="AudioServerAnnouncementFaultTests"/>.
/// </summary>
public sealed class StreamAnnouncementsTests
{
    [Fact]
    public void Announce_ShouldNotCatchOutOfMemory_WhenAnObserverThrowsIt()
    {
        // Arrange
        using var announcements = NewAnnouncements();
        // The one exception type the guard lets through; only the runtime may throw it, so the test raises it itself.
#pragma warning disable CA2201, MA0012
        using var subscription = announcements.Subscribe(static _ => throw new OutOfMemoryException("observer"));
#pragma warning restore CA2201, MA0012

        // Act
        var announce = () => announcements.Announce(Substitute.For<IAudioStream>());

        // Assert
        announce.Should().Throw<OutOfMemoryException>("out of memory is not an observer's failure");
    }

    [Fact]
    public void Complete_ShouldNotCatchOutOfMemory_WhenAnObserverThrowsItOnTheCompletion()
    {
        // Arrange
        using var announcements = NewAnnouncements();
#pragma warning disable CA2201, MA0012
        using var subscription = announcements.Subscribe(static _ => { }, static () => throw new OutOfMemoryException("observer"));
#pragma warning restore CA2201, MA0012

        // Act
        var complete = () => announcements.Complete();

        // Assert
        complete.Should().Throw<OutOfMemoryException>();
    }

    [Fact]
    public void Subscribe_ShouldThrowArgumentNullException_WhenTheObserverIsNull()
    {
        // Arrange
        using var announcements = NewAnnouncements();

        // Act
        var subscribe = () => announcements.Subscribe(null!);

        // Assert
        subscribe.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Announce_ShouldKeepTheObserverThatThrewSubscribed_WhenItThrowsOnEveryStream()
    {
        // Arrange — the direct subscriber that throws, then one that records
        using var announcements = NewAnnouncements();
        var throwerCalls = 0;
        var laterCalls = 0;
        using var thrower = announcements.Subscribe(_ =>
        {
            Interlocked.Increment(ref throwerCalls);
            throw new InvalidOperationException("observer");
        });
        using var later = announcements.Subscribe(_ => Interlocked.Increment(ref laterCalls));

        // Act
        for (var i = 0; i < 3; i++)
            announcements.Announce(Substitute.For<IAudioStream>());

        // Assert
        throwerCalls.Should().Be(3, "a direct subscriber whose handler throws stays subscribed");
        laterCalls.Should().Be(3, "and every later observer receives every stream");
    }

    private static StreamAnnouncements NewAnnouncements() =>
        new(NullLogger.Instance, "[Test]", AudioStreamMetrics.AudioSocketTransport);
}
