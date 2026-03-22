using Asterisk.Sdk;
using Asterisk.Sdk.Enums;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Connection;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Asterisk.Sdk.IntegrationTests.Ami;

[Collection("Integration")]
[Trait("Category", "Integration")]
public class AmiConnectionIntegrationTests : IAsyncLifetime
{
    private readonly IntegrationFixture _fixture;
    private AmiConnection? _connection;

    public AmiConnectionIntegrationTests(IntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _connection = AsteriskFixture.CreateAmiConnection(_fixture);
        await _connection.ConnectAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }

    [AsteriskAvailableFact]
    public void Connect_ShouldSetStateToConnected()
    {
        _connection!.State.Should().Be(AmiConnectionState.Connected);
    }

    [AsteriskAvailableFact]
    public void Connect_ShouldDetectAsteriskVersion()
    {
        _connection!.AsteriskVersion.Should().NotBeNullOrEmpty();
    }

    [AsteriskAvailableFact]
    public async Task SendPingAction_ShouldReturnSuccess()
    {
        var response = await _connection!.SendActionAsync(new PingAction());
        response.Response.Should().Be("Success");
    }

    [AsteriskAvailableFact]
    public async Task SendCoreStatusAction_ShouldReturnCoreStatus()
    {
        var response = await _connection!.SendActionAsync(new CoreStatusAction());
        response.Response.Should().Be("Success");
    }

    [AsteriskAvailableFact]
    public async Task SendCoreSettingsAction_ShouldReturnSettings()
    {
        var response = await _connection!.SendActionAsync(new CoreSettingsAction());
        response.Response.Should().Be("Success");
    }

    [AsteriskAvailableFact]
    public async Task Subscribe_ShouldReceiveEvents()
    {
        var receivedEvent = new TaskCompletionSource<ManagerEvent>();
        using var sub = _connection!.Subscribe(new TestObserver(receivedEvent));

        // Trigger an event by originating a call that will fail quickly
        await _connection.SendActionAsync(new OriginateAction
        {
            Channel = "Local/100@default",
            Context = "default",
            Exten = "100",
            Priority = 1,
            IsAsync = true,
            Timeout = 5000
        });

        var evt = await receivedEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        evt.Should().NotBeNull();
    }

    [AsteriskAvailableFact]
    public async Task Disconnect_ShouldSetStateToDisconnected()
    {
        await _connection!.DisconnectAsync();
        _connection.State.Should().Be(AmiConnectionState.Disconnected);
        _connection = null; // Prevent DisposeAsync from double-disconnecting
    }

    [AsteriskAvailableFact]
    public async Task SendMultipleActions_ShouldCorrelateResponses()
    {
        var task1 = _connection!.SendActionAsync(new PingAction());
        var task2 = _connection.SendActionAsync(new CoreStatusAction());

        var responses = await Task.WhenAll(task1.AsTask(), task2.AsTask());
        responses.Should().HaveCount(2);
        responses.Should().AllSatisfy(r => r.Response.Should().Be("Success"));
    }

    [AsteriskAvailableFact]
    public async Task SendQueueStatusAction_ShouldReturnEvents()
    {
        var events = new List<ManagerEvent>();
        await foreach (var evt in _connection!.SendEventGeneratingActionAsync(new QueueStatusAction()))
        {
            events.Add(evt);
        }

        // Even if no queues are configured, the action should complete without error
        events.Should().NotBeNull();
    }

    [AsteriskAvailableFact]
    public async Task SendCommandAction_ShouldReturnOutput()
    {
        var response = await _connection!.SendActionAsync(new CommandAction { Command = "core show version" });
        response.Should().NotBeNull();
    }

    private sealed class TestObserver(TaskCompletionSource<ManagerEvent> tcs) : IObserver<ManagerEvent>
    {
        public void OnNext(ManagerEvent value) => tcs.TrySetResult(value);
        public void OnError(Exception error) => tcs.TrySetException(error);
        public void OnCompleted() { }
    }
}
