namespace Verbara.Sdk.Ari.Audio;

/// <summary>
/// The count <see cref="AudioServerOptions.MaxConcurrentStreams"/> bounds: the connections held open by
/// every audio server built with one <see cref="AudioServerOptions"/> instance. A connection enters at
/// its accept, before anything is read from it, and leaves when its handler ends, so a connection that
/// has not identified itself yet and one that waits for an id another connection holds are both
/// counted.
/// </summary>
internal sealed class AudioStreamAdmission
{
    private int _held;

    /// <summary>The connections admitted and not yet ended.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>
    /// Admits one connection unless <paramref name="limit"/> are already held. The check and the
    /// increment are one atomic step, so a burst of accepts can never pass the limit between them.
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

    /// <summary>Releases the place of a connection <see cref="TryEnter"/> admitted. Called once per admission.</summary>
    public void Exit() => Interlocked.Decrement(ref _held);
}
