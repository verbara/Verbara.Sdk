using Asterisk.Sdk;
using Asterisk.Sdk.Agi.Mapping;
using Asterisk.Sdk.Agi.Server;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Sdk.IntegrationTests.Agi;

[Trait("Category", "Integration")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public class FastAgiIntegrationTests : IClassFixture<AsteriskFixture>, IAsyncLifetime
{
    private readonly AsteriskFixture _fixture;
    private Asterisk.Sdk.Ami.Connection.AmiConnection? _amiConnection;
    private FastAgiServer? _agiServer;
    private readonly SimpleMappingStrategy _strategy = new();

    public FastAgiIntegrationTests(AsteriskFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _amiConnection = _fixture.CreateAmiConnection();
        await _amiConnection.ConnectAsync();

        _agiServer = new FastAgiServer(_fixture.AgiPort, _strategy, NullLogger<FastAgiServer>.Instance);
        await _agiServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_agiServer is not null) await _agiServer.DisposeAsync();
        if (_amiConnection is not null) await _amiConnection.DisposeAsync();
    }

    [AsteriskAvailableFact]
    public async Task AgiServer_ShouldAcceptConnection_WhenAsteriskCallsAgi()
    {
        var scriptExecuted = new TaskCompletionSource<bool>();

        _strategy.Add("test-script", new TestScript(async (channel, request) =>
        {
            await channel.AnswerAsync();
            scriptExecuted.TrySetResult(true);
        }));

        // Originate a call to extension 200 which invokes AGI
        await _amiConnection!.SendActionAsync(new OriginateAction
        {
            Channel = "Local/200@default",
            Context = "default",
            Exten = "200",
            Priority = 1,
            IsAsync = true,
            Timeout = 10000
        });

        var result = await scriptExecuted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        result.Should().BeTrue();
    }

    [AsteriskAvailableFact]
    public async Task AgiScript_ShouldExecuteGetVariable()
    {
        var variableValue = new TaskCompletionSource<string>();

        _strategy.Add("test-script", new TestScript(async (channel, request) =>
        {
            await channel.AnswerAsync();
            var value = await channel.GetVariableAsync("CHANNEL");
            variableValue.TrySetResult(value);
        }));

        await _amiConnection!.SendActionAsync(new OriginateAction
        {
            Channel = "Local/200@default",
            Context = "default",
            Exten = "200",
            Priority = 1,
            IsAsync = true,
            Timeout = 10000
        });

        var result = await variableValue.Task.WaitAsync(TimeSpan.FromSeconds(15));
        result.Should().NotBeNull();
    }

    [AsteriskAvailableFact]
    public void AgiServer_ShouldBeRunning_AfterStart()
    {
        _agiServer!.IsRunning.Should().BeTrue();
    }

    [AsteriskAvailableFact]
    public async Task AgiServer_ShouldStop_Cleanly()
    {
        await _agiServer!.StopAsync();
        _agiServer.IsRunning.Should().BeFalse();
        _agiServer = null; // Prevent DisposeAsync from double-stopping
    }

    private sealed class TestScript(Func<IAgiChannel, IAgiRequest, ValueTask> handler) : IAgiScript
    {
        public async ValueTask ExecuteAsync(IAgiChannel channel, IAgiRequest request, CancellationToken cancellationToken = default)
        {
            await handler(channel, request);
        }
    }
}
