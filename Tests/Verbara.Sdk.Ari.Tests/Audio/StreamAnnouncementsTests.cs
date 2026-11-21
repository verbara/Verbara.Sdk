using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ari.Audio;
using Verbara.Sdk.Ari.Diagnostics;
using Verbara.Sdk.Ari.Tests.TestSupport;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The guard behind each server's <c>OnStreamConnected</c>, on its own: what it does not catch, and what it refuses.
/// What it catches is measured on the real servers in <see cref="AudioServerAnnouncementFaultTests"/>.
/// </summary>
public sealed class StreamAnnouncementsTests
{
    // The published names, as literals: a rename is a red here.
    private const string SubscriberFaults = "audio.stream_connected.subscriber_faults";
    private const string TransportTag = "audio.transport";

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

    [Theory]
    [InlineData("Where")]
    [InlineData("Select")]
    public void Announce_ShouldEndAComposedSubscriptionOnItsFirstFailureAndKeepTheDirectOne_WhenBothThrowOnEveryStream(
        string operatorName)
    {
        // Arrange — a tag of this test's own, so the process-wide counter is read for these announcements only
        var transport = new KeyValuePair<string, object?>(TransportTag, "test-" + Guid.NewGuid().ToString("N"));
        using var recorder = new AudioMeasurementRecorder(SubscriberFaults);
        using var announcements = new StreamAnnouncements(NullLogger.Instance, "[Test]", transport);
        var composedCalls = 0;
        var directCalls = 0;
        IObservable<IAudioStream> composed = operatorName == "Where"
            ? announcements.Where(static _ => true)
            : announcements.Select(static stream => stream);
        using var composedSubscription = composed.Subscribe(_ =>
        {
            Interlocked.Increment(ref composedCalls);
            throw new InvalidOperationException("composed observer");
        });
        using var direct = announcements.Subscribe(_ =>
        {
            Interlocked.Increment(ref directCalls);
            throw new InvalidOperationException("direct observer");
        });

        // Act
        for (var i = 0; i < 3; i++)
            announcements.Announce(Substitute.For<IAudioStream>());

        // Assert
        var faults = recorder.Of(SubscriberFaults).Where(m => Equals(m.Tag(TransportTag), transport.Value)).Sum(m => m.Value);
        using (new AssertionScope())
        {
            composedCalls.Should().Be(1, "the operator ends its subscription on the first exception its handler throws");
            directCalls.Should().Be(3, "a direct subscriber whose handler throws stays subscribed");
            faults.Should().Be(4, "the composed failure is counted once, and each of the direct one's three");
        }
    }

    private static StreamAnnouncements NewAnnouncements() =>
        new(NullLogger.Instance, "[Test]", AudioStreamMetrics.AudioSocketTransport);
}
