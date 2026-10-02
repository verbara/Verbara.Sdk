namespace Verbara.Sdk.VoiceAi.AudioSocket.Internal;

/// <summary>
/// The count <see cref="AudioSocketOptions.MaxConcurrentSessions"/> bounds: the places taken by the
/// channel ids an <see cref="AudioSocketServer"/> is serving or about to serve. A place is taken after a
/// connection has identified itself and before it registers, and given back once, when the last
/// connection sharing that id is released or refused.
/// </summary>
/// <remarks>
/// The same shape as the admission count of the Ari audio servers, copied rather than referenced: the
/// two packages version separately and this one takes no dependency on the other.
/// </remarks>
internal sealed class AudioSocketAdmission
{
    private int _held;

    /// <summary>The places taken and not yet given back.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>
    /// Takes one place unless <paramref name="limit"/> are already taken. The check and the increment
    /// are one atomic step, so a burst can never pass the limit between them.
    /// </summary>
    public bool TryEnter(int limit)
    {
        while (true)
        {
            var held = Volatile.Read(ref _held);
            if (held >= limit)
                return false;
            if (Interlocked.CompareExchange(ref _held, held + 1, held) == held)
                return true;
        }
    }

    /// <summary>Gives back a place <see cref="TryEnter"/> took. Called once per place.</summary>
    public void Exit() => Interlocked.Decrement(ref _held);
}
