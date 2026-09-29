using System.Globalization;

namespace Verbara.Sdk.VoiceAi.Internal;

/// <summary>
/// Bounds how long a streaming client waits for its vendor once the client has said it has nothing
/// more to send.
/// </summary>
/// <remarks>
/// <para>
/// A streaming WebSocket client ends a session by sending its end-of-input signal (an in-band
/// terminator, a flush or end-of-stream frame, or a close frame) and then receiving until the vendor
/// ends the session. Nothing but the caller's token bounded that wait, so a vendor that never answered
/// (a stalled service, or a connection that died without a FIN) held the caller's receive, and the
/// session handler above it, for good.
/// </para>
/// <para>
/// <see cref="Arm"/> starts the bound once the end-of-input signal is out. A client that bounds the
/// vendor's <em>silence</em> calls <see cref="Heard"/> on every frame the vendor sends, which restarts
/// the limit: a long synthesis that keeps streaming is never cut, and a vendor that stops talking is.
/// A client that bounds the wait for one answer (the OpenAI Realtime bridge waiting for the vendor to
/// answer its close) does not call <see cref="Heard"/>, so a vendor that keeps talking cannot extend
/// the wait. When the limit passes, <see cref="Token"/> is cancelled. That cancels only the receive,
/// which aborts the socket (platform contract), and <see cref="Expired"/> tells the receive loop that
/// this bound, not the caller, ended the wait. The receive loop reports the bound however it leaves,
/// including after a read that returned data with the socket aborted under it.
/// </para>
/// <para>
/// <see cref="Pause"/> and <see cref="Resume"/> hold the bound while the receive loop is busy with
/// work of its own and is not reading, such as the Realtime bridge running a function call the vendor
/// requested. A vendor frame that lands meanwhile waits in the socket: the vendor is not silent, the
/// loop is. <see cref="Resume"/> restarts the full limit. A client that never pauses is unaffected.
/// </para>
/// <para>
/// The limit runs on a <see cref="TimeProvider"/>, <see cref="TimeProvider.System"/> unless one is
/// given, so a test drives it with a manual clock instead of waiting it out. <see cref="Token"/> comes
/// from a source built on that clock with no delay, which <see cref="Arm"/> and the other members set
/// through <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>. The outer token reaches it
/// through a registration that <see cref="Dispose"/> removes along with the source.
/// </para>
/// </remarks>
internal sealed class EndOfInputSilenceBound : IDisposable
{
    /// <summary>
    /// Ten seconds: an order of magnitude above the ~1 s every measured vendor took to answer an end
    /// of input, and far below "until the host is restarted".
    /// </summary>
    internal static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenRegistration _outerRegistration;
    private readonly CancellationToken _outer;
    private readonly TimeSpan _limit;
    private readonly Lock _gate = new();
    private bool _armed;
    private bool _paused;

    /// <summary>A bound on the system clock.</summary>
    /// <param name="limit">How long the vendor may take after the end of input.</param>
    /// <param name="outer">The token the receive loop would otherwise have used; still honoured.</param>
    public EndOfInputSilenceBound(TimeSpan limit, CancellationToken outer)
        : this(limit, TimeProvider.System, outer)
    {
    }

    /// <summary>A bound on the given clock.</summary>
    /// <param name="limit">How long the vendor may take after the end of input.</param>
    /// <param name="timeProvider">The clock the limit runs on.</param>
    /// <param name="outer">The token the receive loop would otherwise have used; still honoured.</param>
    public EndOfInputSilenceBound(TimeSpan limit, TimeProvider timeProvider, CancellationToken outer)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _outer = outer;
        _limit = limit;
        _cts = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        _outerRegistration = outer.UnsafeRegister(
            static state => ((CancellationTokenSource)state!).Cancel(),
            _cts);
    }

    /// <summary>The token the receive loop passes to <c>ReceiveAsync</c>.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>The limit this bound was built with.</summary>
    public TimeSpan Limit => _limit;

    /// <summary>Starts the bound. Call once the end-of-input signal has been sent.</summary>
    public void Arm()
    {
        lock (_gate)
        {
            _armed = true;
            if (!_paused) Restart();
        }
    }

    /// <summary>
    /// Restarts the bound, if it is armed and not paused. A client that bounds the vendor's silence
    /// calls it on every frame the vendor sends.
    /// </summary>
    public void Heard()
    {
        lock (_gate)
        {
            if (_armed && !_paused) Restart();
        }
    }

    /// <summary>
    /// Holds the bound while the receive loop is not reading. An armed bound stops counting; one not yet
    /// armed stays unarmed until <see cref="Arm"/>, and does not start while paused.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            if (_armed) Stop();
        }
    }

    /// <summary>Ends a <see cref="Pause"/>; an armed bound restarts the full limit.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            if (_armed) Restart();
        }
    }

    /// <summary>
    /// True when this bound, not the outer token, ended the wait. A receive loop reads it in its
    /// cancellation handler to tell a silent vendor from a caller that cancelled.
    /// </summary>
    public bool Expired => _cts.IsCancellationRequested && !_outer.IsCancellationRequested;

    /// <summary>The typed failure a silent vendor is reported as: the result is incomplete (<c>ADR-0050</c> E2c).</summary>
    public SpeechProviderFailureException ToFailure(string provider)
        => SpeechProviderFailureException.FromTransport(
            provider,
            new TimeoutException(string.Create(
                CultureInfo.InvariantCulture,
                $"{provider} sent nothing for {_limit.TotalSeconds:0.#} s after the end of input and did not end the session.")));

    private void Restart() => Schedule(_limit);

    private void Stop() => Schedule(Timeout.InfiniteTimeSpan);

    private void Schedule(TimeSpan due)
    {
        try
        {
            _cts.CancelAfter(due);
        }
        catch (ObjectDisposedException)
        {
            // The session already ended and released this bound; there is nothing left to wait for.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The registration first: once it is gone, a late cancel of the outer token cannot reach the
        // source this disposes next.
        _outerRegistration.Dispose();
        _cts.Dispose();
    }
}
