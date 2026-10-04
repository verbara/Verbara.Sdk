using System.Globalization;

namespace Verbara.Sdk.VoiceAi.AudioSocket.Internal;

/// <summary>
/// The <see cref="AudioSocketOptions.ConnectionTimeout"/> an <see cref="AudioSocketServer"/> can use to bound
/// its wait for a connection's identification frame: more than zero and at most
/// <see cref="MaxMilliseconds"/>, or <see cref="Timeout.InfiniteTimeSpan"/> for no deadline.
/// </summary>
/// <remarks>
/// Every value the rule rejects failed every connection before the rule existed: zero refused every
/// identified peer, and a negative value other than <see cref="Timeout.InfiniteTimeSpan"/> or one above
/// the bound threw for each connection, which served nothing. Every value it accepts serves as before.
/// </remarks>
internal static class ConnectionTimeoutRule
{
    /// <summary>
    /// The longest timeout, in milliseconds, that <c>new CancellationTokenSource(TimeSpan, TimeProvider)</c>
    /// accepts — the per-connection deadline is built that way — which is <c>uint.MaxValue - 1</c>
    /// (49.71 days). Measured on the server: 4 294 967 294 ms serves every identified peer, and one more
    /// millisecond throws <see cref="ArgumentOutOfRangeException"/> for each connection. It is not
    /// <c>int.MaxValue</c> ms, which is <see cref="Task.Delay(TimeSpan)"/>'s limit, not this one.
    /// </summary>
    internal const long MaxMilliseconds = 4_294_967_294L;

    /// <summary>The option the rule checks, as the constructor reports it.</summary>
    private static readonly string Member = nameof(AudioSocketOptions.ConnectionTimeout);

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> whose <see cref="ArgumentException.ParamName"/> is
    /// <c>ConnectionTimeout</c>, naming the value and the accepted range, when the value cannot bound the wait.
    /// </summary>
    internal static void ThrowIfUnusable(TimeSpan value)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return;

        if (value > TimeSpan.Zero && value.TotalMilliseconds <= MaxMilliseconds)
            return;

        throw new ArgumentOutOfRangeException(
            Member,
            value,
            string.Create(CultureInfo.InvariantCulture,
                $"{Member} = {value.ToString("c", CultureInfo.InvariantCulture)} cannot bound the wait for a connection's identification frame: it must be more than zero and at most {MaxMilliseconds} ms, or Timeout.InfiniteTimeSpan for no deadline."));
    }
}
