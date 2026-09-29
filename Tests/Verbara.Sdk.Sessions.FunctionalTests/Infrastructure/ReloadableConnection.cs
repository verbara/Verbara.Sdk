using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Live.Server;

namespace Verbara.Sdk.Sessions.FunctionalTests.Infrastructure;

/// <summary>
/// A substitute <see cref="IAmiConnection"/> for a <see cref="VerbaraServer"/> that a test can make
/// reconnect, and whose reload it answers with the snapshots the test gives it.
/// </summary>
/// <remarks>
/// <para>
/// Every state-load action returns nothing unless a reload is running: the load at
/// <see cref="VerbaraServer.StartAsync"/> finds an empty Asterisk. <see cref="ReloadAsync"/> raises
/// <see cref="IAmiConnection.Reconnected"/>, and <c>OnReconnected</c> then sends <c>Status</c>,
/// <c>QueueStatus</c> and <c>Agents</c>, in that order: the first two are answered with the given
/// frames and the third with nothing.
/// </para>
/// <para>
/// <c>OnReconnected</c> is <c>async void</c>, so it cannot be awaited. <c>Agents</c> is the last action
/// it sends, after the queue snapshot has been read to the end, so the reload is taken as done when that
/// action is enumerated, as <c>ReconnectReloadTests</c> takes it. The wait for that signal is bounded only
/// to turn a reload that never ran into a report instead of a hang; no test waits on the clock.
/// </para>
/// <para>
/// The server subscribes a new observer on every reconnect. <see cref="Observer"/> is always the latest
/// one, so a test keeps delivering to the observer the reload left in place.
/// </para>
/// </remarks>
internal sealed class ReloadableConnection
{
    /// <summary>Bounds the wait on the reload's own completion signal; never reached when it runs.</summary>
    private static readonly TimeSpan ReloadBound = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly List<string> _actionsSeen = [];
    private IObserver<ManagerEvent>? _observer;
    private IReadOnlyList<ManagerEvent> _status = [];
    private IReadOnlyList<ManagerEvent> _queueStatus = [];
    private TaskCompletionSource? _reloadDone;

    public ReloadableConnection()
    {
        Connection = Substitute.For<IAmiConnection>();
        Connection.AsteriskVersion.Returns("22.9.0");
        Connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0)));
        Connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(o => Volatile.Write(ref _observer, o)))
            .Returns(_ => Substitute.For<IDisposable>());
    }

    /// <summary>The substitute to build the server on.</summary>
    public IAmiConnection Connection { get; }

    /// <summary>The event observer the server subscribed last.</summary>
    public IObserver<ManagerEvent> Observer =>
        Volatile.Read(ref _observer)
        ?? throw new InvalidOperationException("The server subscribed no event observer: it was not started.");

    /// <summary>
    /// Makes the connection reconnect, answers the reload's <c>Status</c> with <paramref name="status"/>
    /// and its <c>QueueStatus</c> with <paramref name="queueStatus"/>, and returns once the reload has
    /// read both.
    /// </summary>
    public async Task ReloadAsync(IReadOnlyList<ManagerEvent> status, IReadOnlyList<ManagerEvent> queueStatus)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _status = status;
            _queueStatus = queueStatus;
            _actionsSeen.Clear();
            _reloadDone = done;
        }

        Connection.Reconnected += Raise.Event<Action>();

        try
        {
            await done.Task.WaitAsync(ReloadBound);
        }
        catch (TimeoutException ex)
        {
            // The reload never reached its last action. OnReconnected swallows every exception into a
            // log line, so report what it did send rather than a bare timeout.
            string actions;
            lock (_gate)
            {
                actions = _actionsSeen.Count == 0 ? "(none)" : string.Join(", ", _actionsSeen);
            }

            throw new InvalidOperationException($"The reconnect reload never completed. Actions the server sent: {actions}.", ex);
        }
        finally
        {
            lock (_gate)
            {
                _status = [];
                _queueStatus = [];
                _reloadDone = null;
            }
        }
    }

    private async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action)
    {
        await Task.Yield();

        IReadOnlyList<ManagerEvent> answer;
        TaskCompletionSource? done;
        lock (_gate)
        {
            _actionsSeen.Add(action.GetType().Name);
            answer = action switch
            {
                StatusAction => _status,
                QueueStatusAction => _queueStatus,
                _ => [],
            };
            done = action is AgentsAction ? _reloadDone : null;
        }

        foreach (var evt in answer)
            yield return evt;

        done?.TrySetResult();
    }
}
