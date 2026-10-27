namespace Verbara.Sdk.VoiceAi.Stt.Tests.Deepgram;

/// <summary>How a session that withheld a message's final fragment goes on once the test decides.</summary>
internal enum WithheldMessageEnding
{
    /// <summary>The rest of the message is sent, and the session ends as it normally does.</summary>
    Release,

    /// <summary>The fake closes the session with its configured status, the message left unfinished.</summary>
    Close,

    /// <summary>The fake aborts the transport under the unfinished message, with no close frame.</summary>
    Abort,
}

/// <summary>
/// A message <see cref="DeepgramFakeServer"/> sends as its first fragment only, holding the rest until
/// the test says how the session goes on: finish the message, close, or abort.
/// </summary>
/// <remarks>
/// <para>
/// The fake sends it in answer to the client's end-of-input terminator, and only once the test calls
/// <see cref="SendFirstFragment"/>: a test that drives the recognizer's end-of-input bound on a manual
/// clock first waits for that bound to arm, and only then lets a vendor frame in, so the read of the
/// fragment restarts an armed bound and the test can see that it was read.
/// </para>
/// <para>
/// Every wait on the fake's side is released by the server's own token as well, so a test that never
/// decides cannot hold a session past the fake's disposal.
/// </para>
/// </remarks>
internal sealed class WithheldFinalFragment
{
    private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstFragmentSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WithheldMessageEnding> _ending = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="message">The whole text message.</param>
    /// <param name="firstFragmentBytes">How many of its UTF-8 bytes go out as the first fragment.</param>
    public WithheldFinalFragment(string message, int firstFragmentBytes = 16)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstFragmentBytes, 1);
        if (firstFragmentBytes >= System.Text.Encoding.UTF8.GetByteCount(message))
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstFragmentBytes), "The first fragment must leave part of the message withheld.");
        }

        Message = message;
        FirstFragmentBytes = firstFragmentBytes;
    }

    /// <summary>The whole text message.</summary>
    public string Message { get; }

    /// <summary>How many UTF-8 bytes of <see cref="Message"/> the first fragment carries.</summary>
    public int FirstFragmentBytes { get; }

    /// <summary>Completes once the first fragment has been written to the socket.</summary>
    public Task FirstFragmentSent => _firstFragmentSent.Task;

    /// <summary>Lets the fake send the first fragment once it has read the client's terminator.</summary>
    public void SendFirstFragment() => _go.TrySetResult();

    /// <summary>Sends the rest of the message; the session then ends as it normally does.</summary>
    public void Release() => _ending.TrySetResult(WithheldMessageEnding.Release);

    /// <summary>Closes the session with the fake's configured status, the message unfinished.</summary>
    public void Close() => _ending.TrySetResult(WithheldMessageEnding.Close);

    /// <summary>Aborts the transport under the unfinished message.</summary>
    public void Abort() => _ending.TrySetResult(WithheldMessageEnding.Abort);

    internal Task WaitForFirstFragmentAsync(CancellationToken ct) => _go.Task.WaitAsync(ct);

    internal void MarkFirstFragmentSent() => _firstFragmentSent.TrySetResult();

    internal Task<WithheldMessageEnding> WaitForEndingAsync(CancellationToken ct) => _ending.Task.WaitAsync(ct);
}
