using System.Collections.Concurrent;
using Verbara.Sdk.VoiceAi.AudioSocket;
using Verbara.Sdk.VoiceAi.Pipeline;

namespace Verbara.Sdk.VoiceAi.Tests.Pipeline;

/// <summary>
/// A session handler that records every call, with the token it was handed, the moment it is
/// entered, then parks until that token is cancelled or the test calls <see cref="Release"/>.
/// </summary>
/// <remarks>
/// <para>
/// The record is taken synchronously, before the first <c>await</c>. The broker starts its handler
/// inline, from inside its own <see cref="AudioSocketServer.OnSessionStarted"/> delegate, and the
/// server invokes that multicast delegate in subscription order. So once a
/// <see cref="SessionStartedProbe"/> subscribed after the broker has fired for a session,
/// <see cref="Calls"/> already holds every call the broker made for it, and an empty
/// <see cref="Calls"/> is a fact rather than a silence waited out (design D8). A record taken after
/// an <c>await</c> would let a "not handed on" assertion pass vacuously.
/// </para>
/// <para>
/// Parking stands in for a handler stuck in a provider call: it ignores its session ending and
/// ends only on its token or on <see cref="Release"/>, so the token is what a test observes. It is
/// deliberately not disposable, because a container that tracked it would then end the park
/// itself. A test that is done with it calls <see cref="Release"/>.
/// </para>
/// </remarks>
internal sealed class ParkingSessionHandler : ISessionHandler
{
    private readonly ConcurrentQueue<ParkedCall> _calls = new();
    private readonly TaskCompletionSource<ParkedCall> _firstCall =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every call so far, in the order the handler was entered.</summary>
    public IReadOnlyCollection<ParkedCall> Calls => _calls;

    /// <summary>Completes with the first call, as soon as it is recorded.</summary>
    public Task<ParkedCall> FirstCall => _firstCall.Task;

    /// <summary>Ends every park, current and future. Safe to call more than once.</summary>
    public void Release() => _release.TrySetResult();

    public async ValueTask HandleSessionAsync(AudioSocketSession session, CancellationToken ct = default)
    {
        var call = new ParkedCall(session.ChannelId, ct);
        _calls.Enqueue(call);
        _firstCall.TrySetResult(call);

        try
        {
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            call.End(ParkedCallEnding.Released);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The token ended the park. Like the SDK's own handlers (ADR-0054), this one treats a
            // cancelled session as completed, so the broker's fault continuation never runs.
            call.End(ParkedCallEnding.Cancelled);
        }
    }
}

/// <summary>One call to <see cref="ParkingSessionHandler"/>, recorded on entry.</summary>
internal sealed class ParkedCall(Guid channelId, CancellationToken token)
{
    private readonly TaskCompletionSource<ParkedCallEnding> _ended =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The channel of the session the handler was handed.</summary>
    public Guid ChannelId { get; } = channelId;

    /// <summary>The token the handler was handed for this call.</summary>
    public CancellationToken Token { get; } = token;

    /// <summary>Completes when the park ends, with what ended it.</summary>
    public Task<ParkedCallEnding> Ended => _ended.Task;

    internal void End(ParkedCallEnding ending) => _ended.TrySetResult(ending);
}

/// <summary>What ended a <see cref="ParkedCall"/>.</summary>
internal enum ParkedCallEnding
{
    /// <summary>The test called <see cref="ParkingSessionHandler.Release"/>.</summary>
    Released,

    /// <summary>The token the handler was handed was cancelled.</summary>
    Cancelled,
}
