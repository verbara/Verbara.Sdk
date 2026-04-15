namespace Asterisk.Sdk.FunctionalTests.Layer5_Integration.Metrics;

using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Events;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Fixtures;
using Asterisk.Sdk.FunctionalTests.Infrastructure.Helpers;
using FluentAssertions;

/// <summary>
/// Integration tests that verify AMI metrics (counters and histogram) are recorded
/// correctly when actions are sent and events are received via a live Asterisk connection.
/// </summary>
[Collection("Functional")]
[Trait("Category", "Functional")]
public sealed class AmiMetricsTests : FunctionalTestBase
{
    public AmiMetricsTests() : base("Asterisk.Sdk.Ami")
    {
    }

    /// <summary>
    /// Sending PingActions should increment the ami.actions.sent counter by at least the number sent.
    /// </summary>
    [Fact]
    public async Task ActionsSent_ShouldIncrementOnSendAction()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var before = MetricsCapture.Get("ami.actions.sent");

        await connection.SendActionAsync(new PingAction());
        await connection.SendActionAsync(new PingAction());
        await connection.SendActionAsync(new PingAction());

        var after = MetricsCapture.Get("ami.actions.sent");
        var delta = after - before;

        delta.Should().BeGreaterThanOrEqualTo(3,
            "sending 3 PingActions must increment ami.actions.sent by at least 3");
    }

    /// <summary>
    /// Originating a call should cause Asterisk to emit events, incrementing ami.events.received.
    /// </summary>
    [Fact]
    public async Task EventsReceived_ShouldIncrementOnIncomingEvents()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var before = MetricsCapture.Get("ami.events.received");

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "1",
            IsAsync = true,
            ActionId = "metrics-events-received"
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        var after = MetricsCapture.Get("ami.events.received");
        var delta = after - before;

        delta.Should().BeGreaterThan(0,
            "originating a call must produce AMI events that increment ami.events.received");
    }

    /// <summary>
    /// When an observer is subscribed, dispatched events should increment ami.events.dispatched.
    /// </summary>
    [Fact]
    public async Task EventsDispatched_ShouldIncrementWhenObserverSubscribed()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(15);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        // Subscribe an observer so events get dispatched
        using var subscription = connection.Subscribe(new NoOpEventObserver());

        var before = MetricsCapture.Get("ami.events.dispatched");

        await connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@test-functional",
            Application = "Wait",
            Data = "1",
            IsAsync = true,
            ActionId = "metrics-events-dispatched"
        });

        await Task.Delay(TimeSpan.FromSeconds(3));

        var after = MetricsCapture.Get("ami.events.dispatched");
        var delta = after - before;

        delta.Should().BeGreaterThan(0,
            "events dispatched to observers must increment ami.events.dispatched");
    }

    /// <summary>
    /// Sending a PingAction should record a roundtrip measurement in the ami.action.roundtrip histogram.
    /// </summary>
    [Fact]
    public async Task ActionRoundtrip_ShouldRecordHistogram()
    {
        await using var connection = AmiConnectionFactory.Create(LoggerFactory, opts =>
        {
            opts.DefaultResponseTimeout = TimeSpan.FromSeconds(10);
            opts.AutoReconnect = false;
        });
        await connection.ConnectAsync();

        var before = MetricsCapture.GetDouble("ami.action.roundtrip");

        await connection.SendActionAsync(new PingAction());

        var after = MetricsCapture.GetDouble("ami.action.roundtrip");
        var delta = after - before;

        delta.Should().BeGreaterThan(0,
            "sending a PingAction must record a roundtrip measurement in ami.action.roundtrip");
    }

    /// <summary>No-op observer used to ensure event dispatch path is exercised.</summary>
    private sealed class NoOpEventObserver : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) { }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
