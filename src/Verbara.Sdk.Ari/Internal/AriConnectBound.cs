using System.Globalization;
using System.Net.WebSockets;

namespace Verbara.Sdk.Ari.Internal;

/// <summary>
/// Bounds a dial of the ARI events socket made by the reconnect loop — the TCP dial and the HTTP
/// upgrade — so an Asterisk that accepts the connection and never answers the upgrade fails the
/// attempt instead of holding it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClientWebSocket.ConnectAsync(Uri, CancellationToken)"/> has no deadline of its own. Before
/// this bound a stalled upgrade held the reconnect loop on its first dial for good: one attempt, state
/// <c>Reconnecting</c>, no further dial even after the far end recovered.
/// </para>
/// <para>
/// An expiry while the caller's token is still live is reported as a failed upgrade — a
/// <see cref="WebSocketException"/> carrying a <see cref="TimeoutException"/>, the shape a refused dial
/// already takes — so the reconnect loop logs it, counts it as an attempt and dials again on its backoff.
/// A cancellation the caller asked for still arrives as <see cref="OperationCanceledException"/>, which
/// is what stops the loop. Which of the two ended the dial is read from the caller's token, never from
/// the exception (<c>ADR-0053</c>). The same rule as <c>Verbara.Sdk.VoiceAi.Internal.WebSocketConnectBound</c>;
/// a copy because this package does not reference that one.
/// </para>
/// <para>
/// Only the reconnect loop's dial goes through here. The caller's own dial in
/// <c>AriClient.ConnectAsync</c> is not bounded by it: that one still waits for as long as the caller's
/// token allows.
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
    /// <param name="ct">The caller's token.</param>
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
            // The bound fired, not the caller: the far end took the dial and never finished the
            // upgrade. Read from the caller's own token, never from the exception's (ADR-0053).
            var seconds = limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            throw new WebSocketException(
                WebSocketError.Faulted,
                $"The ARI WebSocket upgrade to {uri.Host} did not complete within {seconds} s.",
                new TimeoutException($"No upgrade answer within {seconds} s.", ex));
        }
    }
}
