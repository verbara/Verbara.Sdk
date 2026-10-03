using System.Globalization;
using System.Net.WebSockets;

namespace Verbara.Sdk.VoiceAi.Internal;

/// <summary>
/// Bounds a WebSocket client's connect — the TCP dial, TLS and the HTTP upgrade — so a far end that
/// accepts the connection and never answers the upgrade fails the call instead of holding it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClientWebSocket.ConnectAsync(Uri, CancellationToken)"/> has no deadline of its own: a
/// peer that completes the TCP handshake and then says nothing holds it until the token fires. Most of
/// the WebSocket speech clients bounded it with a linked source and let the expiry escape as a raw
/// <see cref="OperationCanceledException"/>, the type the caller's own cancellation also takes and the
/// one <c>SpeechRecognizer.StreamAsync</c> and <c>SpeechSynthesizer.SynthesizeAsync</c> document as
/// "the token was cancelled". The others had no bound at all.
/// </para>
/// <para>
/// An expiry while the caller's token is still live is reported as what it is: a connection upgrade
/// that failed without an HTTP answer, a <see cref="WebSocketException"/> carrying a
/// <see cref="TimeoutException"/>, the type <see cref="ClientWebSocket"/> gives a refused connection.
/// Every call site therefore keeps its existing classification of a failed connect: the speech clients
/// wrap it as <c>SpeechProviderFailureException.FromHandshake</c> (<c>ADR-0050</c> E7). The caller's
/// own cancellation still arrives as <see cref="OperationCanceledException"/> (<c>ADR-0050</c> E6).
/// Which of the two ended the connect is read from the caller's token, never from the exception
/// (<c>ADR-0053</c>).
/// </para>
/// <para>
/// The limit runs on a <see cref="TimeProvider"/>, the client's own, so a test drives it with a manual
/// clock instead of waiting it out. The limit's source is built on that clock before the dial starts,
/// and is released when the connect ends: it bounds the connect and nothing after it.
/// </para>
/// </remarks>
internal static class WebSocketConnectBound
{
    /// <summary>
    /// The bound for a client that has no option of its own. Five seconds is the default every
    /// option-backed speech client in this SDK ships (<c>ConnectTimeoutSeconds = 5</c>).
    /// </summary>
    internal static readonly TimeSpan Default = TimeSpan.FromSeconds(5);

    /// <summary>Connects <paramref name="ws"/> to <paramref name="uri"/> within <paramref name="limit"/>.</summary>
    /// <param name="ws">The socket to connect.</param>
    /// <param name="uri">Where to connect it.</param>
    /// <param name="limit">How long the dial, TLS and the upgrade may take together.</param>
    /// <param name="timeProvider">The clock <paramref name="limit"/> runs on.</param>
    /// <param name="ct">The caller's token.</param>
    /// <exception cref="WebSocketException">
    /// The upgrade failed, including, with a <see cref="TimeoutException"/> inside, because the far end
    /// did not complete it within <paramref name="limit"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="limit"/> is zero or less, or longer than <see cref="int.MaxValue"/> milliseconds; nothing is dialled.
    /// </exception>
    internal static async Task ConnectAsync(
        ClientWebSocket ws, Uri uri, TimeSpan limit, TimeProvider timeProvider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ws);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // A backstop for any caller that hands an unusable limit: each speech client checks its own option first,
        // naming it, but a limit of zero or less would fail every dial as a timed-out upgrade, and one past what a
        // timer accepts would throw from the timer naming its own parameter.
        if (limit <= TimeSpan.Zero || limit.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit, "The connect limit must be greater than zero and at most int.MaxValue milliseconds.");
        }

        using var bound = new CancellationTokenSource(limit, timeProvider);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);
        try
        {
            await ws.ConnectAsync(uri, connect.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The bound fired, not the caller: the far end took the connection and never finished the
            // upgrade. Read from the caller's own token, never from the exception's (ADR-0053).
            var seconds = limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            throw new WebSocketException(
                WebSocketError.Faulted,
                $"The WebSocket upgrade to {uri.Host} did not complete within {seconds} s.",
                new TimeoutException($"No upgrade answer within {seconds} s.", ex));
        }
    }
}
