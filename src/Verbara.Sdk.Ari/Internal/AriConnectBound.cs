using System.Globalization;
using System.Net.WebSockets;

namespace Verbara.Sdk.Ari.Internal;

/// <summary>
/// Bounds a dial of the ARI events socket — the TCP dial and the HTTP upgrade — so an Asterisk that
/// accepts the connection and never answers the upgrade fails the dial instead of holding it. Both
/// dials go through here: the caller's own in <c>AriClient.ConnectAsync</c> and each dial of the
/// reconnect loop.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClientWebSocket.ConnectAsync(Uri, CancellationToken)"/> has no deadline of its own. Without
/// this bound a stalled upgrade held the caller's <c>ConnectAsync</c> in <c>Connecting</c> for as long as
/// its token allowed, and held the reconnect loop on its first dial for good: one attempt, state
/// <c>Reconnecting</c>, no further dial even after the far end recovered.
/// </para>
/// <para>
/// An expiry while the token handed in is still live is reported as a failed upgrade — a
/// <see cref="WebSocketException"/> carrying a <see cref="TimeoutException"/>, the shape a refused dial
/// already takes — so the caller's dial faults and the reconnect loop logs it, counts it as an attempt
/// and dials again on its backoff. A cancellation of that token still arrives as
/// <see cref="OperationCanceledException"/>, which is what a withdrawn dial and a stopped loop expect.
/// Which of the two ended the dial is read from that token, never from the exception: a cancellation
/// raised inside the transport carries a token nobody here held. The same rule as
/// <c>Verbara.Sdk.VoiceAi.Internal.WebSocketConnectBound</c>; a copy because this package does not
/// reference that one.
/// </para>
/// <para>
/// The limit runs on a <see cref="TimeProvider"/>, the client's own, so a test drives it with a manual
/// clock instead of waiting it out. The limit's source is built on that clock before the dial starts,
/// and is released when the connect ends: it bounds the connect and nothing after it.
/// </para>
/// </remarks>
internal static class AriConnectBound
{
    /// <summary>
    /// Five seconds: the default of every other connect bound in this SDK (<c>AmiConnectionOptions.ConnectionTimeout</c>,
    /// the speech clients' <c>ConnectTimeoutSeconds</c>). A local Asterisk 22.9 answered the upgrade in
    /// 0.2–17 ms over 30 connects (median 0.4 ms).
    /// </summary>
    internal static readonly TimeSpan Default = TimeSpan.FromSeconds(5);

    /// <summary>Connects <paramref name="ws"/> to <paramref name="uri"/> within <paramref name="limit"/>.</summary>
    /// <param name="ws">The socket to connect.</param>
    /// <param name="uri">Where to connect it.</param>
    /// <param name="limit">How long the dial and the upgrade may take together.</param>
    /// <param name="timeProvider">The clock <paramref name="limit"/> runs on.</param>
    /// <param name="ct">The dial's own token: the caller's, or a source linked to it.</param>
    /// <exception cref="WebSocketException">
    /// The upgrade failed, including, with a <see cref="TimeoutException"/> inside, because the far end
    /// did not complete it within <paramref name="limit"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    internal static async Task ConnectAsync(
        ClientWebSocket ws, Uri uri, TimeSpan limit, TimeProvider timeProvider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ws);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(timeProvider);

        using var bound = new CancellationTokenSource(limit, timeProvider);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);
        try
        {
            await ws.ConnectAsync(uri, connect.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The bound fired, not the token handed in: the far end took the dial and never finished
            // the upgrade. Read from that token, never from the exception's own.
            var seconds = limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            throw new WebSocketException(
                WebSocketError.Faulted,
                $"The ARI WebSocket upgrade to {uri.Host} did not complete within {seconds} s.",
                new TimeoutException($"No upgrade answer within {seconds} s.", ex));
        }
    }
}
