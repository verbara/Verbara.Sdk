using FluentAssertions;
using Verbara.Sdk.Sessions.Internal;
using Verbara.Sdk.Sessions.Manager;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Sessions.Tests.Manager;

/// <summary>
/// The engine disposed more than once: a host whose container tracks the engine as itself and a decorator that
/// forwards its disposal disposes it twice, and the teardown must complete.
/// </summary>
public sealed class CallSessionManagerDisposalTests
{
    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenCalledTwice()
    {
        var manager = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new InMemorySessionStore());
        var completions = new CompletionCounter();
        using var subscription = manager.Events.Subscribe(completions);
        await manager.DisposeAsync();

        var act = async () => await manager.DisposeAsync();

        await act.Should().NotThrowAsync("a second disposal returns without doing anything");
        completions.Count.Should().Be(1, "the event stream completed once, at the first disposal");
    }

    private sealed class CompletionCounter : IObserver<SessionDomainEvent>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void OnNext(SessionDomainEvent value)
        {
            // Only the completion is counted.
        }

        public void OnError(Exception error)
        {
            // The manager's subject never faults.
        }

        public void OnCompleted() => Interlocked.Increment(ref _count);
    }
}
