namespace Verbara.Sdk;

/// <summary>
/// The default <see cref="IAmiConnection.Subscribe(Func{ManagerEvent, CancellationToken, ValueTask})"/>: the handler
/// subscribed through the connection's own <see cref="IAmiConnection.OnEvent"/>, called with a token that is never
/// cancelled. Its first disposal removes exactly the adapter it added; later disposals do nothing.
/// </summary>
internal sealed class OnEventTokenSubscription : IDisposable
{
    private readonly Func<ManagerEvent, ValueTask> _adapter;
    private IAmiConnection? _connection;

    private OnEventTokenSubscription(IAmiConnection connection, Func<ManagerEvent, CancellationToken, ValueTask> handler)
    {
        _connection = connection;
        // A new delegate per subscription, so removing it removes this subscription and no other.
        _adapter = evt => handler(evt, CancellationToken.None);
    }

    public static OnEventTokenSubscription Attach(IAmiConnection connection, Func<ManagerEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new OnEventTokenSubscription(connection, handler);
        connection.OnEvent += subscription._adapter;
        return subscription;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _connection, null) is { } connection)
            connection.OnEvent -= _adapter;
    }
}
