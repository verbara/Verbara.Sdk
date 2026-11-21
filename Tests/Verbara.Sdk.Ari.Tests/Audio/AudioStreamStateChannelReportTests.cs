using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The report of an audio stream's <c>StateChanges</c> observer failure is not a second failure: when the logger throws
/// while writing the Error line, the notification still goes on to the next observer and nothing reaches the party that
/// published it (the read pump, the stream's ending, its disposal).
/// </summary>
/// <remarks>
/// Every observer subscribes before anything is published, on the test's thread, and each throwing observer throws only
/// on a publication, never on the replay of the initial state: the replay's exception is handed back to the subscribing
/// call by design, and is not what this class is about.
/// </remarks>
public sealed class AudioStreamStateChannelReportTests
{
    [Fact]
    public void PublishConnected_ShouldReachTheNextObserver_WhenAnObserverThrowsAndTheLoggerThrowsOnTheReport()
    {
        using var channel = new AudioStreamStateChannel(AudioStreamState.Disconnected, new ThrowingLogger(), () => "chan-1");
        var thrower = new ThrowingObserver(throwOn: AudioStreamState.Connected);
        var next = new RecordingObserver();
        using var first = channel.Subscribe(thrower);
        using var second = channel.Subscribe(next);

        var publishing = Record.Exception(channel.PublishConnected);

        using var scope = new AssertionScope();
        publishing.Should().BeNull("a failing report changes nothing: the publication returns normally");
        next.States.Should().Equal(
            [AudioStreamState.Disconnected, AudioStreamState.Connected],
            "the next observer receives the state the first one threw on");
        channel.Value.Should().Be(AudioStreamState.Connected);
    }

    [Fact]
    public void TryEnd_ShouldPublishTheWholeEndingToTheNextObserver_WhenAnObserverThrowsAndTheLoggerThrowsOnTheReport()
    {
        using var channel = new AudioStreamStateChannel(AudioStreamState.Connected, new ThrowingLogger(), () => "chan-1");
        var thrower = new ThrowingObserver(throwOn: AudioStreamState.Error);
        var next = new RecordingObserver();
        using var first = channel.Subscribe(thrower);
        using var second = channel.Subscribe(next);

        var ended = false;
        var ending = Record.Exception(() => ended = channel.TryEnd(error: true));

        using var scope = new AssertionScope();
        ending.Should().BeNull("a failing report changes nothing: the ending is published past it");
        ended.Should().BeTrue("this call published the ending");
        next.States.Should().Equal(
            [AudioStreamState.Connected, AudioStreamState.Error, AudioStreamState.Disconnected],
            "the next observer receives Error and then Disconnected");
        thrower.Calls.Should().Be(3, "the throwing observer stays subscribed: the replay, Error and Disconnected");
        channel.Ended.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldCompleteTheNextObserver_WhenAnObserverThrowsOnTheCompletionAndTheLoggerThrowsOnTheReport()
    {
        var channel = new AudioStreamStateChannel(AudioStreamState.Connected, new ThrowingLogger(), () => "chan-1");
        var thrower = new ThrowingObserver(throwOn: null, throwOnCompletion: true);
        var next = new RecordingObserver();
        using var first = channel.Subscribe(thrower);
        using var second = channel.Subscribe(next);

        var disposing = Record.Exception(channel.Dispose);

        using var scope = new AssertionScope();
        disposing.Should().BeNull("a failing report changes nothing: the disposal completes");
        next.Completions.Should().Be(1, "the next observer receives the completion");
    }

    [Fact]
    public void PublishConnected_ShouldLogOneErrorAndReachTheNextObserver_WhenAnObserverThrowsAndTheLoggerWorks()
    {
        var logger = new RecordingLogger();
        using var channel = new AudioStreamStateChannel(AudioStreamState.Disconnected, logger, () => "chan-1");
        var failure = new InvalidOperationException("state-observer");
        var thrower = new ThrowingObserver(throwOn: AudioStreamState.Connected, failure);
        var next = new RecordingObserver();
        using var first = channel.Subscribe(thrower);
        using var second = channel.Subscribe(next);

        channel.PublishConnected();

        using var scope = new AssertionScope();
        next.States.Should().EndWith(AudioStreamState.Connected);
        logger.Errors.Should().ContainSingle("one line per failure").Which.Should().BeSameAs(failure);
    }

    [Fact]
    public void PublishConnected_ShouldLetOutOfMemoryThrough_WhenTheLoggerThrowsItOnTheReport()
    {
        using var channel = new AudioStreamStateChannel(
            AudioStreamState.Disconnected, new ThrowingLogger(outOfMemory: true), () => "chan-1");
        using var first = channel.Subscribe(new ThrowingObserver(throwOn: AudioStreamState.Connected));

        var publishing = channel.PublishConnected;

        publishing.Should().Throw<OutOfMemoryException>("the report's guard does not hide an out-of-memory failure");
    }

    // The only exception the guard lets through is one only the runtime may throw; a test has to raise it itself.
#pragma warning disable CA1859, CA2201, MA0012
    private static Exception OutOfMemory() => new OutOfMemoryException();
#pragma warning restore CA1859, CA2201, MA0012

    private sealed class ThrowingObserver(
        AudioStreamState? throwOn, Exception? failure = null, bool throwOnCompletion = false) : IObserver<AudioStreamState>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void OnNext(AudioStreamState value)
        {
            var call = Interlocked.Increment(ref _calls);
            // Never on the replay (the first call); only on the state named.
            if (call > 1 && throwOn is { } state && value == state)
                throw failure ?? new InvalidOperationException("state-observer");
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
            if (throwOnCompletion)
                throw new InvalidOperationException("completion-observer");
        }
    }

    private sealed class RecordingObserver : IObserver<AudioStreamState>
    {
        private readonly Lock _gate = new();
        private readonly List<AudioStreamState> _states = [];
        private int _completions;

        public IReadOnlyList<AudioStreamState> States
        {
            get { lock (_gate) return [.. _states]; }
        }

        public int Completions => Volatile.Read(ref _completions);

        public void OnNext(AudioStreamState value)
        {
            lock (_gate) _states.Add(value);
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted() => Interlocked.Increment(ref _completions);
    }

    private sealed class ThrowingLogger(bool outOfMemory = false) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw (outOfMemory ? OutOfMemory() : new InvalidOperationException("the log provider failed"));
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<Exception?> _errors = [];

        public IReadOnlyList<Exception?> Errors
        {
            get { lock (_gate) return [.. _errors]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                lock (_gate) _errors.Add(exception);
            }
        }
    }
}
