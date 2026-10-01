using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Verbara.Sdk.Ami.Tests.Connection;

/// <summary>
/// A notification handler that has not returned once 30 seconds have passed is logged once, at Warning, with the words
/// "has not returned after 30 s; later notifications wait for it" — and nothing else changes: the queue stays in order,
/// and no handler that returned before the bound is ever reported.
/// </summary>
/// <remarks>
/// <para>
/// The bound is a fixed 30 seconds read from the connection's clock, an internal <see cref="TimeProvider"/> passed to an
/// internal constructor overload; the public constructor uses <see cref="TimeProvider.System"/>. The tests pass a
/// <see cref="FakeTimeProvider"/> and move it past the bound instead of waiting it out, so no real time and no run count
/// is the evidence: each assertion is a position of the fake clock.
/// </para>
/// <para>
/// The constructor is internal and reached by reflection (a test project is not AOT-published, and this keeps the test
/// compiling against a connection that does not have it yet). The stuck notification is a loss, with
/// <see cref="AmiConnectionOptions.AutoReconnect"/> off, so no backoff runs on the fake clock.
/// </para>
/// </remarks>
public sealed class AmiConnectionStuckNotificationTests
{
    /// <summary>A hang bound. Every wait ends on its signal long before it; only a defect reaches it.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>The owner's bound: fixed, not an option.</summary>
    private static readonly TimeSpan StuckBound = TimeSpan.FromSeconds(30);

    private const string StuckLine = "has not returned after 30 s; later notifications wait for it";

    [Fact]
    public async Task Notify_ShouldLogOneWarning_WhenAHandlerHasNotReturnedOnceTheBoundHasPassed()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        using var stuck = new ManualResetEventSlim(false);
        var sockets = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var clock = new FakeTimeProvider();
        var connection = Create(sockets, logger, clock);
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Lost += _ =>
            {
                entered.TrySetResult();
                stuck.Wait(peerCts.Token);
            };
            var served = ServeFirstAsync(sockets, peerCts.Token);
            await connection.ConnectAsync().AsTask().WaitAsync(Bound);
            await served.WaitAsync(Bound);
            sockets.Created[0].CloseFromPeer();
            await entered.Task.WaitAsync(Bound);

            clock.Advance(StuckBound - TimeSpan.FromMilliseconds(1));
            var belowTheBound = StuckWarnings(logger);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var atTheBound = StuckWarnings(logger);
            clock.Advance(StuckBound * 10);
            var wellPastTheBound = StuckWarnings(logger);

            using (new AssertionScope())
            {
                belowTheBound.Should().BeEmpty("the handler has run for less than 30 s on the connection's clock");
                atTheBound.Should().ContainSingle("a handler still running at 30 s is reported once")
                    .Which.Level.Should().Be(LogLevel.Warning);
                wellPastTheBound.Should().HaveCount(1, "the same stuck handler is reported once, however long it runs");
            }
        }
        finally
        {
            stuck.Set();
            await connection.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Notify_ShouldLogNoWarning_WhenEveryHandlerReturnedBeforeTheBound()
    {
        using var peerCts = new CancellationTokenSource(Bound * 3);
        var sockets = new PipedSocketFactory();
        var logger = new SignalingLogger<AmiConnection>();
        var clock = new FakeTimeProvider();
        var connection = Create(sockets, logger, clock);
        try
        {
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Lost += _ => delivered.TrySetResult();
            var served = ServeFirstAsync(sockets, peerCts.Token);
            await connection.ConnectAsync().AsTask().WaitAsync(Bound);
            await served.WaitAsync(Bound);
            sockets.Created[0].CloseFromPeer();
            await delivered.Task.WaitAsync(Bound);
            await connection.PendingNotifications.WaitAsync(Bound);

            clock.Advance(StuckBound * 10);

            StuckWarnings(logger).Should().BeEmpty("every handler returned before the connection's clock reached the bound");
        }
        finally
        {
            await connection.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static List<(LogLevel Level, string Line)> StuckWarnings(SignalingLogger<AmiConnection> logger) =>
        [.. logger.Entries.Where(entry => entry.Line.Contains(StuckLine, StringComparison.Ordinal))];

    /// <summary>The connection, built through the internal constructor that takes its clock.</summary>
    private static AmiConnection Create(PipedSocketFactory sockets, ILogger<AmiConnection> logger, TimeProvider clock)
    {
        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = "secret",
            EnableHeartbeat = false,
            AutoReconnect = false,
        });
        var constructor = ClockConstructor(typeof(AmiConnection));
        constructor.Should().NotBeNull(
            "AmiConnection takes its clock through an internal constructor overload, so the stuck-handler bound can be moved past on a fake clock");
        return (AmiConnection)constructor!.Invoke([options, sockets, logger, clock]);
    }

    private static ConstructorInfo? ClockConstructor(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type type) =>
        type.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(IOptions<AmiConnectionOptions>), typeof(ISocketConnectionFactory), typeof(ILogger<AmiConnection>), typeof(TimeProvider)]);

    /// <summary>Plays the peer of the first socket through the login.</summary>
    private static Task ServeFirstAsync(PipedSocketFactory sockets, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var socket = await sockets.NextAsync(ct);
            await socket.CompleteLoginAsync(ct);
        }, ct);
}
