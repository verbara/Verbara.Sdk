using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;

namespace Verbara.Sdk.Live.Tests.Harness;

/// <summary>
/// An <see cref="IAmiConnection"/> that is not an <see cref="AmiConnection"/>, as a host's adapter or decorator is: it
/// forwards every member the interface declared before a caller could learn how an event-generating action ended to the
/// real connection it wraps, and nothing else. A live server over it reads that connection only through the interface.
/// </summary>
internal class PlainAmiConnectionWrapper(AmiConnection inner) : IAmiConnection
{
    /// <summary>The real connection, over the in-memory harness.</summary>
    protected AmiConnection Inner { get; } = inner;

    public AmiConnectionState State => Inner.State;

    public string? AsteriskVersion => Inner.AsteriskVersion;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => Inner.ConnectAsync(cancellationToken);

    public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default) =>
        Inner.SendActionAsync(action, cancellationToken);

    public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
        where TResponse : ManagerResponse => Inner.SendActionAsync<TResponse>(action, cancellationToken);

    public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(ManagerAction action,
        CancellationToken cancellationToken = default) => Inner.SendEventGeneratingActionAsync(action, cancellationToken);

    public IDisposable Subscribe(IObserver<ManagerEvent> observer) => Inner.Subscribe(observer);

    public event Func<ManagerEvent, ValueTask>? OnEvent
    {
        add => Inner.OnEvent += value;
        remove => Inner.OnEvent -= value;
    }

    public event Action? Reconnected
    {
        add => Inner.Reconnected += value;
        remove => Inner.Reconnected -= value;
    }

    public event Action<AmiConnectionStateChange>? StateChanged
    {
        add => Inner.StateChanged += value;
        remove => Inner.StateChanged -= value;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => Inner.DisconnectAsync(cancellationToken);

    public ValueTask DisposeAsync() => Inner.DisposeAsync();
}

/// <summary>
/// A wrapper that forwards every member of <see cref="IAmiConnection"/> to the real connection, including the members by
/// which a caller learns how an event-generating action ended. It re-implements the interface so that a member declared
/// here implements the interface's member of the same signature, in place of the interface's default.
/// </summary>
/// <remarks>
/// Before the interface declares those members it forwards exactly what <see cref="PlainAmiConnectionWrapper"/> does.
/// When they are added (the capability and the overload that takes an outcome), they are forwarded here, and only here.
/// </remarks>
internal sealed class ForwardingAmiConnectionWrapper(AmiConnection inner) : PlainAmiConnectionWrapper(inner), IAmiConnection;
