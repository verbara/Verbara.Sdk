using FluentAssertions;
using FluentAssertions.Execution;
using Verbara.Sdk;

namespace Verbara.Sdk.Ari.Tests.Resources;

/// <summary>
/// The external media create that takes an <see cref="AriExternalMediaRequest"/> is a default member of
/// <see cref="IAriChannelsResource"/>, so a resource written against 2.9.0 still compiles. Its body forwards a request
/// that names neither added option to the shipped overload, and refuses one that names either, instead of dropping it.
/// </summary>
public sealed class AriChannelsResourceDefaultMemberTests
{
    [Fact]
    public async Task CreateExternalMediaAsync_ShouldForwardEveryValueToTheShippedOverload_WhenTheRequestNamesNeitherAddedOption()
    {
        // Arrange — a resource with exactly the members IAriChannelsResource had in 2.9.0
        var resource = new ResourceWrittenAgainst290();
        IAriChannelsResource channels = resource;
        using var cts = new CancellationTokenSource();
        var request = new AriExternalMediaRequest
        {
            App = "app",
            ExternalHost = "cmedia",
            Format = "slin16",
            Encapsulation = "none",
            Transport = "websocket",
            ConnectionType = "client",
            Direction = "both",
            Data = "data-1",
            ChannelId = "chan-1",
        };

        // Act
        var channel = await channels.CreateExternalMediaAsync(request, cts.Token);

        // Assert
        using (new AssertionScope())
        {
            channel.Should().BeSameAs(resource.Returned);
            resource.Calls.Should().ContainSingle().Which.Should().Be(
                ("app", "cmedia", "slin16", "none", "websocket", "client", "both", "data-1", "chan-1"),
                "each value reaches the parameter of its own name");
            resource.Tokens.Should().ContainSingle().Which.Should().Be(cts.Token);
        }
    }

    [Fact]
    public async Task CreateExternalMediaAsync_ShouldForwardToTheShippedOverload_WhenVariablesIsEmpty()
    {
        // Arrange
        var resource = new ResourceWrittenAgainst290();
        IAriChannelsResource channels = resource;
        var request = new AriExternalMediaRequest
        {
            App = "app", ExternalHost = "cmedia", Format = "slin16", Variables = new Dictionary<string, string>(),
        };

        // Act
        await channels.CreateExternalMediaAsync(request);

        // Assert — an empty set of variables is no option: nothing would be sent for it
        resource.Calls.Should().ContainSingle().Which.Should().Be(
            ("app", "cmedia", "slin16", null, null, null, null, null, null));
    }

    [Fact]
    public async Task CreateExternalMediaAsync_ShouldThrowNotSupportedNamingTheOption_WhenTheRequestNamesTransportData()
    {
        // Arrange
        var resource = new ResourceWrittenAgainst290();
        IAriChannelsResource channels = resource;
        var request = new AriExternalMediaRequest { App = "app", ExternalHost = "cmedia", Format = "slin16", TransportData = "f(json)" };

        // Act
        var act = async () => await channels.CreateExternalMediaAsync(request);

        // Assert
        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain("TransportData");
        resource.Calls.Should().BeEmpty("the option is refused, not dropped, so no request is sent");
    }

    [Fact]
    public async Task CreateExternalMediaAsync_ShouldThrowNotSupportedNamingTheOption_WhenTheRequestNamesVariables()
    {
        // Arrange
        var resource = new ResourceWrittenAgainst290();
        IAriChannelsResource channels = resource;
        var request = new AriExternalMediaRequest
        {
            App = "app", ExternalHost = "cmedia", Format = "slin16",
            Variables = new Dictionary<string, string> { ["LABEMVAR"] = "v" },
        };

        // Act
        var act = async () => await channels.CreateExternalMediaAsync(request);

        // Assert
        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain("Variables");
        resource.Calls.Should().BeEmpty("the option is refused, not dropped, so no request is sent");
    }

    [Fact]
    public async Task CreateExternalMediaAsync_ShouldThrowArgumentNull_WhenTheRequestIsNull()
    {
        // Arrange
        var resource = new ResourceWrittenAgainst290();
        IAriChannelsResource channels = resource;

        // Act
        var act = async () => await channels.CreateExternalMediaAsync(null!);

        // Assert
        (await act.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("request");
        resource.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Implements exactly the members <see cref="IAriChannelsResource"/> had in 2.9.0, as a third party would have. Only
    /// the shipped external media create is reachable from these facts; every other member fails the test if called.
    /// </summary>
    private sealed class ResourceWrittenAgainst290 : IAriChannelsResource
    {
        public AriChannel Returned { get; } = new() { Id = "chan-1" };

        public List<(string App, string ExternalHost, string Format, string? Encapsulation, string? Transport,
            string? ConnectionType, string? Direction, string? Data, string? ChannelId)> Calls { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public ValueTask<AriChannel> CreateExternalMediaAsync(string app, string externalHost, string format,
            string? encapsulation = null, string? transport = null, string? connectionType = null,
            string? direction = null, string? data = null, string? channelId = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((app, externalHost, format, encapsulation, transport, connectionType, direction, data, channelId));
            Tokens.Add(cancellationToken);
            return ValueTask.FromResult(Returned);
        }

        private static InvalidOperationException NotReached() => new("Not reached by these facts.");

        public ValueTask<AriChannel[]> ListAsync(CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriChannel> CreateAsync(string endpoint, string? app = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriChannel> GetAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask HangupAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriChannel> OriginateAsync(string endpoint, string? extension = null, string? context = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask RingAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask ProgressAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask AnswerAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriVariable> GetVariableAsync(string channelId, string variable, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask SetVariableAsync(string channelId, string variable, string value, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask HoldAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask UnholdAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask MuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask UnmuteAsync(string channelId, string? direction = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask SendDtmfAsync(string channelId, string dtmf, int? before = null, int? between = null, int? duration = null, int? after = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriPlayback> PlayAsync(string channelId, string media, string? lang = null, int? offsetms = null, int? skipms = null, string? playbackId = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriLiveRecording> RecordAsync(string channelId, string name, string format, int? maxDurationSeconds = null, int? maxSilenceSeconds = null, string? ifExists = null, bool? beep = null, string? terminateOn = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriChannel> SnoopAsync(string channelId, string app, string? spy = null, string? whisper = null, string? snoopId = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask RedirectAsync(string channelId, string endpoint, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask ContinueAsync(string channelId, string? context = null, string? extension = null, int? priority = null, string? label = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriChannel> CreateWithoutDialAsync(string endpoint, string app, string? channelId = null, string? otherChannelId = null, string? originator = null, IReadOnlyDictionary<string, string>? variables = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask MoveAsync(string channelId, string app, string? appArgs = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask DialAsync(string channelId, string? caller = null, int? timeout = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask<AriRtpStats> GetRtpStatisticsAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask SilenceAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask StopSilenceAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask StartMohAsync(string channelId, string? mohClass = null, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask StopMohAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
        public ValueTask StopRingAsync(string channelId, CancellationToken cancellationToken = default) => throw NotReached();
    }
}
