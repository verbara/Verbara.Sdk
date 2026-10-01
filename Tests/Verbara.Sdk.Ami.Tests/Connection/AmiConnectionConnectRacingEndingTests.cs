using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Reflection;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Enums;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A caller's <see cref="AmiConnection.ConnectAsync"/> overtaken by the caller's own ending yields to that ending: it
/// throws <see cref="OperationCanceledException"/>, writes no state after the ending's
/// <see cref="AmiConnectionState.Disconnecting"/>, and leaves nothing it acquired running — never the
/// <see cref="NullReferenceException"/> of a token source or writer the ending released, and never a disposed connection
/// that reads <see cref="AmiConnectionState.Connected"/>.
/// </summary>
/// <remarks>
/// <para>
/// No timing decides the outcome. The socket's input is wrapped by a reader that, once armed, holds the next read it
/// completes — data, the stream's end or a cancellation — until the test releases it. The test arms it at a known point
/// of the login the peer plays, runs <see cref="AmiConnection.DisposeAsync"/> to completion while the attempt is held,
/// and only then lets the attempt go on: the attempt resumes on a connection whose ending has released everything.
/// </para>
/// <para>
/// The event pump an attempt creates is a private field, read by reflection (a test project is not AOT-published, and an
/// <c>extern</c> accessor reads as unmanaged code to the code scan). A renamed field fails the test with
/// <see cref="MissingFieldException"/>.
/// </para>
/// </remarks>
public sealed class AmiConnectionConnectRacingEndingTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>The attempt has read the MD5 challenge and is about to write its login.</summary>
    public const string ChallengeAnswered = "challenge answered";

    /// <summary>The attempt has sent the version probe and waits for its answer, which never comes.</summary>
    public const string VersionProbePending = "version probe pending";

    [Theory]
    [InlineData(ChallengeAnswered)]
    [InlineData(VersionProbePending)]
    public async Task ConnectAsync_ShouldThrowOperationCanceled_WhenTheCallerEndsTheConnectionDuringTheAttempt(string point)
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new HoldingSocketFactory(new PipedSocketFactory());
        await using var connection = Create(sockets);
        var peer = Task.Run(() => PlayUntilAsync(sockets, point, peerCts.Token), peerCts.Token);

        var connecting = connection.ConnectAsync().AsTask();
        var input = await peer.WaitAsync(Bound);

        await connection.DisposeAsync().AsTask().WaitAsync(Bound);
        var stateAfterEnding = connection.State;
        input.Release();
        var thrown = await Record.ExceptionAsync(() => connecting.WaitAsync(Bound));

        using (new AssertionScope())
        {
            stateAfterEnding.Should().Be(AmiConnectionState.Disconnected, "the ending ran to completion while the attempt was held");
            thrown.Should().BeAssignableTo<OperationCanceledException>(
                "the caller's ending cut the attempt short, and the attempt yields to it");
            connection.State.Should().Be(AmiConnectionState.Disconnected,
                "the ending owns the state: the attempt writes nothing after it");
            EventPump(connection).Should().BeNull("the attempt leaves nothing it acquired running once the ending has released");
            sockets.Inner.Created.Should().ContainSingle().Which.DisposeCount.Should().Be(1, "the socket is released once");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static AmiConnection Create(ISocketConnectionFactory sockets) =>
        new(Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = true,
            // Limits, never waits: nothing here runs until them.
            ConnectionTimeout = TimeSpan.FromMinutes(1),
            DefaultResponseTimeout = TimeSpan.FromMinutes(1),
        }), sockets, NullLogger<AmiConnection>.Instance);

    /// <summary>
    /// Plays the peer through the login up to <paramref name="point"/>, arms the hold on the connection's input there,
    /// and hands that input back.
    /// </summary>
    internal static async Task<HoldingReader> PlayUntilAsync(HoldingSocketFactory sockets, string point, CancellationToken ct)
    {
        var socket = await sockets.Inner.NextAsync(ct);
        var input = sockets.Readers[0];
        await socket.WriteAsync("Asterisk Call Manager/6.0.0\r\n");
        var challenge = await socket.ReadActionAsync(ct) ?? throw new InvalidOperationException("No challenge.");
        if (point == ChallengeAnswered)
        {
            // The attempt's read of the challenge's answer completes with it, and the hold keeps it there.
            input.HoldNext();
            await socket.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "abc123")]);
            return input;
        }

        await socket.RespondAsync("Success", PipedSocket.ActionIdOf(challenge), [new("Challenge", "abc123")]);
        var login = await socket.ReadActionAsync(ct) ?? throw new InvalidOperationException("No login.");
        await socket.RespondAsync("Success", PipedSocket.ActionIdOf(login), [new("Message", "Authentication accepted")]);
        _ = await socket.ReadActionAsync(ct) ?? throw new InvalidOperationException("No version probe.");
        if (point != VersionProbePending)
            throw new ArgumentOutOfRangeException(nameof(point), point, "Not a point this test plays.");

        // The attempt waits for the probe's answer: the ending's cleanup ends that read, and the hold keeps it.
        input.HoldNext();
        return input;
    }

    private static object? EventPump(AmiConnection connection) =>
        PrivateField(typeof(AmiConnection), "_eventPump").GetValue(connection);

    private static FieldInfo PrivateField(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields)] Type owner, string name) =>
        owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(owner.FullName, name);

    /// <summary>Wraps every socket <see cref="PipedSocketFactory"/> creates so its input can be held.</summary>
    internal sealed class HoldingSocketFactory(PipedSocketFactory inner) : ISocketConnectionFactory
    {
        private readonly Lock _gate = new();
        private readonly List<HoldingReader> _readers = [];

        public PipedSocketFactory Inner => inner;

        /// <summary>The input of every socket created so far, in creation order.</summary>
        public IReadOnlyList<HoldingReader> Readers
        {
            get
            {
                lock (_gate)
                {
                    return [.. _readers];
                }
            }
        }

        public ISocketConnection Create()
        {
            HoldingSocket socket;
            lock (_gate)
            {
                socket = new HoldingSocket(inner.Create());
                _readers.Add(socket.Reader);
            }

            return socket;
        }

        public ISocketConnection FromStream(Stream stream) => throw new NotSupportedException("The AMI client only dials out.");
    }

    internal sealed class HoldingSocket(ISocketConnection inner) : ISocketConnection
    {
        public HoldingReader Reader { get; } = new(inner.Input);

        public bool IsConnected => inner.IsConnected;

        public PipeReader Input => Reader;

        public PipeWriter Output => inner.Output;

        public ValueTask ConnectAsync(string hostname, int port, bool useSsl = false, CancellationToken cancellationToken = default) =>
            inner.ConnectAsync(hostname, port, useSsl, cancellationToken);

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => inner.CloseAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// A <see cref="PipeReader"/> that delegates to another and, once <see cref="HoldNext"/> has armed it, holds the next
    /// read it completes — its result or its exception — until <see cref="Release"/>.
    /// </summary>
    internal sealed class HoldingReader(PipeReader inner) : PipeReader
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed;

        /// <summary>Holds the next read that completes, including one already in flight.</summary>
        public void HoldNext() => _armed = true;

        public void Release() => _released.TrySetResult();

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadResult result;
            try
            {
                result = await inner.ReadAsync(cancellationToken);
            }
            catch (Exception) when (_armed)
            {
                await _released.Task;
                throw;
            }

            if (_armed)
                await _released.Task;

            return result;
        }

        public override bool TryRead(out ReadResult result) => inner.TryRead(out result);

        public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);

        public override void CancelPendingRead() => inner.CancelPendingRead();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);
    }
}
