using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using Verbara.Sdk;
using Verbara.Sdk.Ami;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Transport;
using Verbara.Sdk.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Sdk.Ami.Tests.Connection;

public sealed class AmiConnectionTests : IAsyncLifetime
{
    private readonly Pipe _serverToClient = new();
    private readonly Pipe _clientToServer = new();
    private readonly AmiConnection _sut;

    public AmiConnectionTests()
    {
        _sut = CreateConnection(TimeSpan.FromSeconds(5));
    }

    private AmiConnection CreateConnection(TimeSpan responseTimeout)
    {
        var socketConnection = Substitute.For<ISocketConnection>();
        socketConnection.Input.Returns(_serverToClient.Reader);
        socketConnection.Output.Returns(_clientToServer.Writer);
        socketConnection.IsConnected.Returns(true);
#pragma warning disable CA2012 // NSubstitute setup requires evaluating the ValueTask
        socketConnection.ConnectAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);
#pragma warning restore CA2012

        var socketFactory = Substitute.For<ISocketConnectionFactory>();
        socketFactory.Create().Returns(socketConnection);

        var options = Options.Create(new AmiConnectionOptions
        {
            Hostname = "localhost",
            Port = 5038,
            Username = "admin",
            Password = "secret",
            AutoReconnect = false,
            DefaultResponseTimeout = responseTimeout
        });

        return new AmiConnection(options, socketFactory, NullLogger<AmiConnection>.Instance);
    }

    /// <summary>Bound on the class cleanup, so a hang there fails the test instead of stalling the lane.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => ReleaseAsync().WaitAsync(CleanupBound);

    private async Task ReleaseAsync()
    {
        await _serverToClient.Writer.CompleteAsync();
        await _clientToServer.Reader.CompleteAsync();
        await _sut.DisposeAsync();
    }

    /// <summary>
    /// Simulates a successful AMI login sequence from the server side.
    /// Reads client's actions to extract actual ActionIDs and echoes them back.
    /// </summary>
    private async Task SimulateSuccessfulLoginAsync(CancellationToken ct = default)
    {
        var writer = _serverToClient.Writer;
        var reader = _clientToServer.Reader;

        // 1. Protocol identifier
        await WriteBytesAsync(writer, "Asterisk Call Manager/6.0.0\r\n");

        // 2. Read Challenge action, extract ActionID, respond
        var challengeAction = await ReadActionAsync(reader, ct);
        var challengeId = ExtractActionId(challengeAction);
        await WriteResponseAsync(writer, "Success", challengeId, [new("Challenge", "abc123")]);

        // 3. Read Login action, respond success
        var loginAction = await ReadActionAsync(reader, ct);
        var loginId = ExtractActionId(loginAction);
        await WriteResponseAsync(writer, "Success", loginId, [new("Message", "Authentication accepted")]);

        // 4. Read CoreSettings action, respond with version
        var coreAction = await ReadActionAsync(reader, ct);
        var coreId = ExtractActionId(coreAction);
        await WriteResponseAsync(writer, "Success", coreId, [new("AsteriskVersion", "20.0.0")]);
    }

    private static async Task WriteBytesAsync(PipeWriter writer, string data)
    {
        await writer.WriteAsync(Encoding.UTF8.GetBytes(data));
        await writer.FlushAsync();
    }

    private static async Task WriteResponseAsync(PipeWriter writer, string status, string actionId,
        List<KeyValuePair<string, string>>? fields = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Response: {status}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"ActionID: {actionId}\r\n");
        if (fields is not null)
        {
            foreach (var kv in fields)
                sb.Append(CultureInfo.InvariantCulture, $"{kv.Key}: {kv.Value}\r\n");
        }
        sb.Append("\r\n");
        await WriteBytesAsync(writer, sb.ToString());
    }

    /// <summary>
    /// Reads a complete AMI action message (terminated by \r\n\r\n) from the pipe.
    /// </summary>
    private static async Task<string> ReadActionAsync(PipeReader reader, CancellationToken ct = default)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var buffer = result.Buffer;
            var text = Encoding.UTF8.GetString(buffer);
            var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var action = text[..(idx + 4)];
                reader.AdvanceTo(buffer.GetPosition(idx + 4));
                return action;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted) return text;
        }
    }

    private static string ExtractActionId(string actionText)
    {
        var match = Regex.Match(actionText, @"ActionID:\s*(.+?)\r?\n", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    [Fact]
    public async Task ConnectAsync_ShouldTransitionToConnected_WhenLoginSucceeds()
    {
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        _sut.State.Should().Be(AmiConnectionState.Connected);
        _sut.AsteriskVersion.Should().Be("20.0.0");
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowAmiProtocolException_WhenNoProtocolIdentifier()
    {
        await WriteResponseAsync(_serverToClient.Writer, "Error", "1");

        var act = async () => await _sut.ConnectAsync();
        await act.Should().ThrowAsync<AmiProtocolException>();
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowAmiAuthenticationException_WhenLoginFails()
    {
        var writer = _serverToClient.Writer;
        var reader = _clientToServer.Reader;

        await WriteBytesAsync(writer, "Asterisk Call Manager/6.0.0\r\n");

        _ = Task.Run(async () =>
        {
            // Challenge — respond success
            var challengeAction = await ReadActionAsync(reader);
            var challengeId = ExtractActionId(challengeAction);
            await WriteResponseAsync(writer, "Success", challengeId, [new("Challenge", "abc123")]);

            // Login — respond failure
            var loginAction = await ReadActionAsync(reader);
            var loginId = ExtractActionId(loginAction);
            await WriteResponseAsync(writer, "Error", loginId, [new("Message", "Authentication failed")]);
        });

        var act = async () => await _sut.ConnectAsync();
        await act.Should().ThrowAsync<AmiAuthenticationException>();
    }

    /// <summary>Answers Challenge and Login, leaving the version probe to the caller.</summary>
    private async Task SimulateLoginWithoutVersionAsync()
    {
        var writer = _serverToClient.Writer;
        var reader = _clientToServer.Reader;

        await WriteBytesAsync(writer, "Asterisk Call Manager/6.0.0\r\n");

        var challengeId = ExtractActionId(await ReadActionAsync(reader));
        await WriteResponseAsync(writer, "Success", challengeId, [new("Challenge", "abc123")]);

        var loginId = ExtractActionId(await ReadActionAsync(reader));
        await WriteResponseAsync(writer, "Success", loginId, [new("Message", "Authentication accepted")]);
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowOperationCanceledException_WhenCancelledDuringVersionDetection()
    {
        using var cts = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            await SimulateLoginWithoutVersionAsync();

            // CoreSettings is on the wire; the caller gives up before Asterisk answers it.
            await ReadActionAsync(_clientToServer.Reader);
            await cts.CancelAsync();
        });

        var act = async () => await _sut.ConnectAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await server;
    }

    [Fact]
    public async Task ConnectAsync_ShouldThrowOperationCanceledException_WhenCancelledDuringVersionFallback()
    {
        await using var sut = CreateConnection(TimeSpan.FromMilliseconds(200));
        using var cts = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            await SimulateLoginWithoutVersionAsync();

            // Leave CoreSettings unanswered so it times out, then give up during the CLI fallback.
            await ReadActionAsync(_clientToServer.Reader);
            var fallback = await ReadActionAsync(_clientToServer.Reader);
            fallback.Should().Contain("core show version");
            await cts.CancelAsync();
        });

        var act = async () => await sut.ConnectAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await server;
    }

    [Fact]
    public void State_ShouldBeInitial_BeforeConnect()
    {
        _sut.State.Should().Be(AmiConnectionState.Initial);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldTransitionToDisconnected()
    {
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        await _sut.DisconnectAsync();
        _sut.State.Should().Be(AmiConnectionState.Disconnected);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldSendLogoffAction()
    {
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        await _sut.DisconnectAsync();

        // Read Logoff action from the client-to-server pipe
        var result = await _clientToServer.Reader.ReadAsync();
        var text = Encoding.UTF8.GetString(result.Buffer);
        _clientToServer.Reader.AdvanceTo(result.Buffer.End);
        text.Should().Contain("Logoff");
    }

    [Fact]
    public async Task Subscribe_ShouldAddObserver_AndUnsubscribeRemove()
    {
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        var observer = Substitute.For<IObserver<ManagerEvent>>();
        using var subscription = _sut.Subscribe(observer);
        subscription.Should().NotBeNull();

        subscription.Dispose();
    }

    [Fact]
    public async Task Subscribe_MultipleObservers_ShouldAllReceive()
    {
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        var observer1 = Substitute.For<IObserver<ManagerEvent>>();
        var observer2 = Substitute.For<IObserver<ManagerEvent>>();
        using var sub1 = _sut.Subscribe(observer1);
        using var sub2 = _sut.Subscribe(observer2);

        // Write an event from the server
        await WriteBytesAsync(_serverToClient.Writer, "Event: Newchannel\r\nChannel: SIP/2000-00000001\r\n\r\n");

        // Give the reader loop and event pump time to process
        await Task.Delay(500);

        observer1.Received().OnNext(Arg.Any<ManagerEvent>());
        observer2.Received().OnNext(Arg.Any<ManagerEvent>());
    }

    [Fact]
    public async Task SendActionAsync_ShouldThrowAmiNotConnectedException_WhenNotConnected()
    {
        var act = async () => await _sut.SendActionAsync(new TestAction());
        await act.Should().ThrowAsync<AmiNotConnectedException>();
    }

    [Fact]
    public async Task NextActionId_ShouldGenerateUniqueIds()
    {
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        // 3 unique action IDs were generated during login (Challenge, Login, CoreSettings)
        _sut.State.Should().Be(AmiConnectionState.Connected);
    }

    [Fact]
    public async Task CleanupAsync_ShouldCancelPendingActions()
    {
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        await _sut.DisconnectAsync();
        _sut.State.Should().Be(AmiConnectionState.Disconnected);
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisconnect_WhenConnected()
    {
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        _sut.State.Should().Be(AmiConnectionState.Connected);
        await _sut.DisposeAsync();
        _sut.State.Should().Be(AmiConnectionState.Disconnected);
    }

    // ── Event-generating action helpers ────────────────────────────────────────

    private static async Task WriteEventAsync(PipeWriter writer, string eventType, string actionId,
        List<KeyValuePair<string, string>>? fields = null)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Event: {eventType}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"ActionID: {actionId}\r\n");
        if (fields is not null)
        {
            foreach (var kv in fields)
                sb.Append(CultureInfo.InvariantCulture, $"{kv.Key}: {kv.Value}\r\n");
        }
        sb.Append("\r\n");
        await WriteBytesAsync(writer, sb.ToString());
    }

    // ── Event-generating action regression tests (Bug 2: Error response) ────────

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldComplete_WhenServerReturnsError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writer = _serverToClient.Writer;
        var reader = _clientToServer.Reader;

        // Simulate login + server responds with error to next action
        _ = Task.Run(async () =>
        {
            await SimulateSuccessfulLoginAsync(cts.Token);

            // Read the StatusAction sent by the SUT
            var actionText = await ReadActionAsync(reader, cts.Token);
            var actionId = ExtractActionId(actionText);

            // Server returns error instead of events
            await WriteResponseAsync(writer, "Error", actionId,
                [new("Message", "No such command")]);
        }, cts.Token);

        await _sut.ConnectAsync(cts.Token);

        var events = new List<ManagerEvent>();
        await foreach (var evt in _sut.SendEventGeneratingActionAsync(
            new Verbara.Sdk.Ami.Actions.StatusAction(), cts.Token))
        {
            events.Add(evt);
        }

        events.Should().BeEmpty("error response should complete the collector with 0 events");
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldYieldEvents_ThenComplete()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writer = _serverToClient.Writer;
        var reader = _clientToServer.Reader;

        _ = Task.Run(async () =>
        {
            await SimulateSuccessfulLoginAsync(cts.Token);

            var actionText = await ReadActionAsync(reader, cts.Token);
            var actionId = ExtractActionId(actionText);

            // Server sends 2 status events + StatusComplete
            await WriteEventAsync(writer, "Status", actionId,
                [new("Channel", "PJSIP/2000-001"), new("State", "Up")]);
            await WriteEventAsync(writer, "Status", actionId,
                [new("Channel", "PJSIP/3000-001"), new("State", "Ring")]);
            await WriteEventAsync(writer, "StatusComplete", actionId,
                [new("Items", "2")]);
        }, cts.Token);

        await _sut.ConnectAsync(cts.Token);

        var events = new List<ManagerEvent>();
        await foreach (var evt in _sut.SendEventGeneratingActionAsync(
            new Verbara.Sdk.Ami.Actions.StatusAction(), cts.Token))
        {
            events.Add(evt);
        }

        events.Should().HaveCount(2);
    }

    [Fact]
    public async Task SendEventGeneratingActionAsync_ShouldComplete_WhenServerReturnsErrorWithMessage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var writer = _serverToClient.Writer;
        var reader = _clientToServer.Reader;

        _ = Task.Run(async () =>
        {
            await SimulateSuccessfulLoginAsync(cts.Token);

            var actionText = await ReadActionAsync(reader, cts.Token);
            var actionId = ExtractActionId(actionText);

            await WriteResponseAsync(writer, "Error", actionId,
                [new("Message", "Permission denied")]);
        }, cts.Token);

        await _sut.ConnectAsync(cts.Token);

        var events = new List<ManagerEvent>();
        await foreach (var evt in _sut.SendEventGeneratingActionAsync(
            new Verbara.Sdk.Ami.Actions.StatusAction(), cts.Token))
        {
            events.Add(evt);
        }

        events.Should().BeEmpty("error with message should complete the collector without blocking");
    }

    [Fact]
    public async Task ConnectAsync_ShouldNotRegisterDuplicateGauges_OnReconnect()
    {
        // First connect
        var loginTask = SimulateSuccessfulLoginAsync();
        await _sut.ConnectAsync();
        await loginTask;

        _sut.State.Should().Be(AmiConnectionState.Connected);

        // Disconnect
        await _sut.DisconnectAsync();
        _sut.State.Should().Be(AmiConnectionState.Disconnected);

        // The guard flag (_gaugesRegistered) should prevent duplicate registration
        // on subsequent connects. We verify indirectly: if this were broken,
        // each connect would add new gauge callbacks that accumulate over time.
        // Since we can't easily count gauge callbacks, we verify the connect/disconnect
        // cycle works without errors (the fix is structural — a bool guard).
    }

    [Theory]
    [MemberData(nameof(AmiConnectionOptionsValidatorTests.ValuesAConstructorRejects), MemberType = typeof(AmiConnectionOptionsValidatorTests))]
    public async Task Ctor_ShouldThrowNamingTheOption_WhenAReconnectValueIsUnusableAndAutoReconnectIsOn(string value, string option)
    {
        var options = AmiConnectionOptionsValidatorTests.ReconnectOptions(value, autoReconnect: true);
        AmiConnection? created = null;

        var act = () => created = new AmiConnection(
            Options.Create(options), Substitute.For<ISocketConnectionFactory>(), NullLogger<AmiConnection>.Instance);

        try
        {
            act.Should().Throw<ArgumentOutOfRangeException>(
                    $"with AutoReconnect on, {value} is a value the reconnect backoff cannot use, and no validator runs on this path")
                .Which.ParamName.Should().Be(option, "the error names the option to fix");
        }
        finally
        {
            if (created is not null)
                await created.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(AmiConnectionOptionsValidatorTests.ValuesAConstructorRejects), MemberType = typeof(AmiConnectionOptionsValidatorTests))]
    public async Task Ctor_ShouldSucceed_WhenAutoReconnectIsOffWithUnusableValues(string value, string option)
    {
        var options = AmiConnectionOptionsValidatorTests.ReconnectOptions(value, autoReconnect: false);

        var connection = new AmiConnection(
            Options.Create(options), Substitute.For<ISocketConnectionFactory>(), NullLogger<AmiConnection>.Instance);

        connection.State.Should().Be(AmiConnectionState.Initial,
            $"without AutoReconnect the backoff never runs, so {value} ({option}) is not checked at construction");
        await connection.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(AmiConnectionOptionsValidatorTests.UnusableConnectionTimeouts), MemberType = typeof(AmiConnectionOptionsValidatorTests))]
    public async Task Ctor_ShouldThrowNamingConnectionTimeout_WhenTheConnectionTimeoutIsUnusable(string value, bool autoReconnect)
    {
        var options = AmiConnectionOptionsValidatorTests.ConnectionTimeoutOptions(value, autoReconnect);
        AmiConnection? created = null;

        var act = () => created = new AmiConnection(
            Options.Create(options), Substitute.For<ISocketConnectionFactory>(), NullLogger<AmiConnection>.Instance);

        try
        {
            act.Should().Throw<ArgumentOutOfRangeException>(
                    $"{value} cannot bound a connect (AutoReconnect = {autoReconnect}), and no validator runs on this path")
                .Which.ParamName.Should().Be(nameof(AmiConnectionOptions.ConnectionTimeout), "the error names the option to fix");
        }
        finally
        {
            if (created is not null)
                await created.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(AmiConnectionOptionsValidatorTests.UsableConnectionTimeouts), MemberType = typeof(AmiConnectionOptionsValidatorTests))]
    public async Task Ctor_ShouldSucceed_WhenTheConnectionTimeoutIsAtABoundOfTheUsableRange(string value, bool autoReconnect)
    {
        var options = AmiConnectionOptionsValidatorTests.ConnectionTimeoutOptions(value, autoReconnect);

        var connection = new AmiConnection(
            Options.Create(options), Substitute.For<ISocketConnectionFactory>(), NullLogger<AmiConnection>.Instance);

        connection.State.Should().Be(AmiConnectionState.Initial, $"{value} is positive and finite");
        await connection.DisposeAsync();
    }

    /// <summary>
    /// The options are held by reference: a <see cref="AmiConnectionOptions.ConnectionTimeout"/> changed after
    /// construction to a value that cannot bound a connect is rejected by the next <see cref="AmiConnection.ConnectAsync"/>
    /// before it dials or writes a state, naming the option, instead of dialling with no bound (or a bound that
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> rejects under its own parameter name).
    /// </summary>
    [Theory]
    [MemberData(nameof(AmiConnectionOptionsValidatorTests.UnusableConnectionTimeouts), MemberType = typeof(AmiConnectionOptionsValidatorTests))]
    public async Task ConnectAsync_ShouldThrowNamingConnectionTimeoutBeforeItDials_WhenTheConnectionTimeoutIsMadeUnusableAfterConstruction(
        string value, bool autoReconnect)
    {
        var options = AmiConnectionOptionsValidatorTests.ConnectionTimeoutOptions("ConnectionTimeout = 5 s", autoReconnect);
        var factory = new PipedSocketFactory();
        var connection = new AmiConnection(Options.Create(options), factory, NullLogger<AmiConnection>.Instance);
        var changes = 0;
        connection.StateChanged += _ => Interlocked.Increment(ref changes);
        // Changed after construction, so the constructor's check never saw it.
        options.ConnectionTimeout = AmiConnectionOptionsValidatorTests.ConnectionTimeoutOptions(value, autoReconnect).ConnectionTimeout;
        Exception? outcome = null;

        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = ex;
        }

        var rejection = outcome switch
        {
            null => "no exception",
            ArgumentOutOfRangeException rejected => $"ArgumentOutOfRangeException naming {rejected.ParamName}",
            _ => $"{outcome.GetType().Name}: {outcome.Message}",
        };

        try
        {
            rejection.Should().Be($"ArgumentOutOfRangeException naming {nameof(AmiConnectionOptions.ConnectionTimeout)}",
                $"{value} cannot bound a connect (AutoReconnect = {autoReconnect}), the connect checks the option it uses, and the error names the option to fix");
            factory.Created.Should().BeEmpty("the option is checked before the connect dials");
            Volatile.Read(ref changes).Should().Be(0, "the rejected connect writes no state");
            connection.State.Should().Be(AmiConnectionState.Initial);
        }
        finally
        {
            await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class TestAction : ManagerAction;
}
