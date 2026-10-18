using System.Diagnostics.Metrics;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.Server;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Sdk.Live.Tests.Server;

/// <summary>
/// Disposing a started server releases its subscription to the AMI connection, the ARI client attached to it and the
/// instruments it published, each whatever an earlier release threw, and then reports what failed.
/// </summary>
public sealed class VerbaraServerDisposeTests
{
    [Fact]
    public async Task DisposeAsync_ShouldReleaseTheInstrumentsAndThrowTheAriClientsException_WhenTheAriClientThrows()
    {
        using var instruments = new ServerInstruments();
        var subscription = Substitute.For<IDisposable>();
        var server = NewServer(subscription);
        var ari = Substitute.For<IAriClient>();
        var ariFailure = new InvalidOperationException("ari");
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        ari.DisposeAsync().Returns(_ => throw ariFailure);
#pragma warning restore CA2012
        server.SetAriClient(ari);
        await instruments.StartAsync(server);

        var act = async () => await server.DisposeAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(ariFailure);
        using (new AssertionScope())
        {
            DisposeCalls(subscription).Should().Be(1, "the subscription is disposed once");
            instruments.Published.Should().BePositive();
            instruments.Completed.Should().Be(instruments.Published, "every instrument the server published is released");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeTheAriClientAndReleaseTheInstruments_WhenTheSubscriptionThrows()
    {
        using var instruments = new ServerInstruments();
        var subscription = Substitute.For<IDisposable>();
        var subscriptionFailure = new InvalidOperationException("subscription");
        subscription.When(s => s.Dispose()).Do(_ => throw subscriptionFailure);
        var server = NewServer(subscription);
        var ari = Substitute.For<IAriClient>();
        server.SetAriClient(ari);
        await instruments.StartAsync(server);

        var act = async () => await server.DisposeAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(subscriptionFailure);
        using (new AssertionScope())
        {
            DisposeCalls(ari).Should().Be(1, "the ARI client is disposed once");
            instruments.Published.Should().BePositive();
            instruments.Completed.Should().Be(instruments.Published, "every instrument the server published is released");
        }
    }

    [Fact]
    public async Task DisposeAsync_ShouldThrowOneAggregateInReleaseOrderAndReleaseTheInstruments_WhenTheSubscriptionAndTheAriClientBothThrow()
    {
        using var instruments = new ServerInstruments();
        var subscription = Substitute.For<IDisposable>();
        var subscriptionFailure = new InvalidOperationException("subscription");
        subscription.When(s => s.Dispose()).Do(_ => throw subscriptionFailure);
        var server = NewServer(subscription);
        var ari = Substitute.For<IAriClient>();
        var ariFailure = new InvalidOperationException("ari");
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        ari.DisposeAsync().Returns(_ => throw ariFailure);
#pragma warning restore CA2012
        server.SetAriClient(ari);
        await instruments.StartAsync(server);

        // Recorded as thrown: FluentAssertions unwraps an aggregate before matching, so it cannot tell one throw from two.
        var thrown = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());

        using (new AssertionScope())
        {
            thrown.Should().BeOfType<AggregateException>();
            (thrown as AggregateException)?.InnerExceptions.Should()
                .Equal([subscriptionFailure, ariFailure], (a, e) => ReferenceEquals(a, e), "the failures come in release order");
            instruments.Published.Should().BePositive();
            instruments.Completed.Should().Be(instruments.Published, "every instrument the server published is released");
        }
    }

    /// <summary>
    /// A listener's completion callback runs inside the meter's disposal, so a listener that throws there makes the
    /// release of the instruments throw too. That failure joins the earlier one instead of replacing it.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ShouldThrowTheAriClientsAndTheListenersExceptionsTogether_WhenAListenerOfTheInstrumentsAlsoThrows()
    {
        using var instruments = new ServerInstruments();
        var listenerFailure = new InvalidOperationException("listener");
        instruments.ThrowOnFirstCompletion(listenerFailure);
        var server = NewServer(Substitute.For<IDisposable>());
        var ari = Substitute.For<IAriClient>();
        var ariFailure = new InvalidOperationException("ari");
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        ari.DisposeAsync().Returns(_ => throw ariFailure);
#pragma warning restore CA2012
        server.SetAriClient(ari);
        await instruments.StartAsync(server);

        var thrown = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());

        using (new AssertionScope())
        {
            instruments.Completed.Should().BePositive("the meter's disposal reached the listener");
            thrown.Should().BeOfType<AggregateException>();
            (thrown as AggregateException)?.InnerExceptions.Should()
                .Equal([ariFailure, listenerFailure], (a, e) => ReferenceEquals(a, e), "the failures come in release order");
        }
    }

    /// <summary>Positive control, green before and after: nothing throws, so every release already runs.</summary>
    [Fact]
    public async Task DisposeAsync_ShouldReturnAndReleaseEverythingOnce_WhenNothingThrows()
    {
        using var instruments = new ServerInstruments();
        var subscription = Substitute.For<IDisposable>();
        var server = NewServer(subscription);
        var ari = Substitute.For<IAriClient>();
        server.SetAriClient(ari);
        await instruments.StartAsync(server);

        var act = async () => await server.DisposeAsync();

        await act.Should().NotThrowAsync();
        using (new AssertionScope())
        {
            DisposeCalls(subscription).Should().Be(1, "the subscription is disposed once");
            DisposeCalls(ari).Should().Be(1, "the ARI client is disposed once");
            instruments.Published.Should().BePositive();
            instruments.Completed.Should().Be(instruments.Published, "every instrument the server published is released");
        }
    }

    private static int DisposeCalls(object substitute) =>
        substitute.ReceivedCalls().Count(c => c.GetMethodInfo().Name is nameof(IDisposable.Dispose) or nameof(IAsyncDisposable.DisposeAsync));

    private static VerbaraServer NewServer(IDisposable subscription)
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.Subscribe(Arg.Any<IObserver<ManagerEvent>>()).Returns(subscription);
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyAsyncEnumerable());
        return new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }

    private static async IAsyncEnumerable<ManagerEvent> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Counts the instruments one server publishes when it starts, and how many of them are released when its meter is
/// disposed. Other servers, started by tests running in parallel, publish instruments with the same names under the
/// same meter name; only the ones created inside <see cref="StartAsync"/> are counted. The server creates them before
/// its first <c>await</c>, so the publication callback runs on the starting flow and sees the flag set around it.
/// </summary>
internal sealed class ServerInstruments : IDisposable
{
    private static readonly AsyncLocal<ServerInstruments?> Starting = new();

    private readonly MeterListener _listener = new();
    private int _published;
    private int _completed;
    private Exception? _completionFailure;

    public ServerInstruments()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (!ReferenceEquals(Starting.Value, this))
                return;
            Interlocked.Increment(ref _published);
            listener.EnableMeasurementEvents(instrument, this);
        };
        _listener.MeasurementsCompleted = (_, state) =>
        {
            if (!ReferenceEquals(state, this))
                return;
            Interlocked.Increment(ref _completed);
            if (Interlocked.Exchange(ref _completionFailure, null) is { } failure)
                throw failure;
        };
        _listener.Start();
    }

    /// <summary>How many instruments the server published while it started.</summary>
    public int Published => Volatile.Read(ref _published);

    /// <summary>How many of those instruments have been released by the disposal of their meter.</summary>
    public int Completed => Volatile.Read(ref _completed);

    /// <summary>
    /// Makes the listener throw <paramref name="failure"/> from its completion callback, once: on the first of this
    /// server's instruments to be released.
    /// </summary>
    public void ThrowOnFirstCompletion(Exception failure) => Volatile.Write(ref _completionFailure, failure);

    public async Task StartAsync(VerbaraServer server)
    {
        Starting.Value = this;
        try
        {
            await server.StartAsync();
        }
        finally
        {
            Starting.Value = null;
        }
    }

    public void Dispose() => _listener.Dispose();
}
