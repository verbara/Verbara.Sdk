using Asterisk.Sdk.Hosting;
using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using Asterisk.Sdk.Sessions.Manager;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Sdk.Hosting.Tests;

public sealed class SessionReconciliationServiceTests : IDisposable
{
    private readonly SessionOptions _options;
    private readonly SessionReconciliationService _sut;
    private readonly ICallSessionManager _manager;

    public SessionReconciliationServiceTests()
    {
        _options = new SessionOptions
        {
            DialingTimeout = TimeSpan.FromSeconds(5),
            RingingTimeout = TimeSpan.FromSeconds(10),
            ReconciliationInterval = TimeSpan.FromSeconds(30),
        };
        _manager = Substitute.For<ICallSessionManager>();
        _manager.ActiveSessions.Returns([]);

        _sut = new SessionReconciliationService(
            _manager,
            Options.Create(_options),
            NullLogger<SessionReconciliationService>.Instance);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public void Sweep_ShouldMarkTimedOut_WhenDialingExceedsTimeout()
    {
        var session = new CallSession("s1", "l1", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        // Simulate DialingAt in the past beyond timeout
        session.DialingAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
        _manager.ActiveSessions.Returns(new[] { session });

        _sut.Sweep();

        session.State.Should().Be(CallSessionState.TimedOut);
    }

    [Fact]
    public void Sweep_ShouldMarkTimedOut_WhenRingingExceedsTimeout()
    {
        var session = new CallSession("s2", "l2", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Ringing);
        // Simulate RingingAt in the past beyond timeout
        session.RingingAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(15);
        _manager.ActiveSessions.Returns(new[] { session });

        _sut.Sweep();

        session.State.Should().Be(CallSessionState.TimedOut);
    }

    [Fact]
    public void Sweep_ShouldMarkOrphaned_WhenCreatedExceedsTimeout()
    {
        // CreatedAt is set via init in constructor to UtcNow, so we need a session
        // created far enough in the past. We create one with a backdated CreatedAt.
        var session = new CallSession("s3", "l3", "srv1", CallDirection.Inbound)
        {
            CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10),
        };
        // Session stays in Created state
        _manager.ActiveSessions.Returns(new[] { session });

        _sut.Sweep();

        session.State.Should().Be(CallSessionState.Failed);
        session.Metadata.Should().ContainKey("cause")
            .WhoseValue.Should().Be("orphaned");
    }

    [Fact]
    public void Sweep_ShouldNotTouch_WhenSessionConnected()
    {
        var session = new CallSession("s4", "l4", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        session.TryTransition(CallSessionState.Connected);
        _manager.ActiveSessions.Returns(new[] { session });

        _sut.Sweep();

        session.State.Should().Be(CallSessionState.Connected);
    }

    [Fact]
    public void Sweep_ShouldNotTouch_WhenDialingWithinTimeout()
    {
        var session = new CallSession("s5", "l5", "srv1", CallDirection.Inbound);
        session.TryTransition(CallSessionState.Dialing);
        // DialingAt is set automatically by TryTransition, should be recent
        _manager.ActiveSessions.Returns(new[] { session });

        _sut.Sweep();

        session.State.Should().Be(CallSessionState.Dialing);
    }

    [Fact]
    public async Task StopAsync_ShouldStopGracefully()
    {
        await _sut.StartAsync(CancellationToken.None);

        var act = () => _sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
