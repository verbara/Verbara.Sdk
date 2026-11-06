using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The chan_websocket control surface that says something Asterisk never sends or acts on is deprecated with one id,
/// <c>VSDK0004</c>, and nothing is removed. Reading the attribute is the point of each fact: a consumer building with
/// <c>TreatWarningsAsErrors</c> gets <c>VSDK0004</c>, not a blanket <c>CS0618</c>, and the message says what to read
/// instead.
/// </summary>
public partial class ChanWebSocketDeprecationTests
{
    private const string DeprecationId = "VSDK0004";

    [Theory]
    [InlineData(nameof(IChanWebSocketSession.SendXonAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendXoffAsync))]
    public void FlowControlSend_ShouldBeObsoleteWithVsdk0004NamingWhereFlowControlComesFrom_WhenAConsumerCallsIt(string method)
    {
        // Arrange
        var member = typeof(IChanWebSocketSession).GetMethod(method, [typeof(CancellationToken)]);
        member.Should().NotBeNull("the method stays on the interface; deprecating it removes nothing");

        // Act
        var obsolete = member!.GetCustomAttribute<ObsoleteAttribute>();

        // Assert
        obsolete.Should().NotBeNull("chan_websocket has no XON/XOFF command in any spelling");
        obsolete!.DiagnosticId.Should().Be(DeprecationId);
        obsolete.IsError.Should().BeFalse("a deprecation warns; it does not break a build without TreatWarningsAsErrors");
        obsolete.Message.Should().Contain("NotSupportedException")
            .And.Contain(nameof(ChanWebSocketMediaXoff))
            .And.Contain(nameof(ChanWebSocketMediaXon))
            .And.Contain(nameof(IChanWebSocketSession.ControlMessages));
    }

    [Fact]
    public void ChanWebSocketHangup_ShouldBeObsoleteWithVsdk0004NamingStateChanges_WhenAConsumerNamesIt()
    {
        // Act
#pragma warning disable VSDK0004 // the obsolete read is the point
        var obsolete = typeof(ChanWebSocketHangup).GetCustomAttribute<ObsoleteAttribute>();
#pragma warning restore VSDK0004

        // Assert
        obsolete.Should().NotBeNull("a hangup is a close frame, never a text message, so nothing publishes this record");
        obsolete!.DiagnosticId.Should().Be(DeprecationId);
        obsolete.IsError.Should().BeFalse();
        obsolete.Message.Should().Contain(nameof(IAudioStream.StateChanges));
    }

    [Fact]
    public void ChanWebSocketMediaBuffering_ShouldBeObsoleteWithVsdk0004NamingXoffXonAndTheStatusReply_WhenAConsumerNamesIt()
    {
        // Act
#pragma warning disable VSDK0004 // the obsolete read is the point
        var obsolete = typeof(ChanWebSocketMediaBuffering).GetCustomAttribute<ObsoleteAttribute>();
#pragma warning restore VSDK0004

        // Assert
        obsolete.Should().NotBeNull("no chan_websocket message reports a buffer fill in bytes");
        obsolete!.DiagnosticId.Should().Be(DeprecationId);
        obsolete.IsError.Should().BeFalse();
        obsolete.Message.Should().Contain(nameof(ChanWebSocketMediaXoff))
            .And.Contain(nameof(ChanWebSocketMediaXon))
            .And.Contain($"{nameof(ChanWebSocketStatus)}.{nameof(ChanWebSocketStatus.QueueLength)}")
            .And.Contain(nameof(IChanWebSocketSession.SendGetStatusAsync));
        obsolete.Message.Replace(nameof(IChanWebSocketSession.SendGetStatusAsync), string.Empty, StringComparison.Ordinal)
            .Should().NotContain("GetStatusAsync", "the session sends GET_STATUS and the reply arrives on ControlMessages; no GetStatusAsync exists");
    }

    public static TheoryData<string> DeprecatedControlSurfaceMembers()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Vsdk0004Messages())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(DeprecatedControlSurfaceMembers))]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test code: looks up a type of the Ari assembly by name, never trimmed here.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test code: reads the public members of the Ari assembly's own types, never trimmed here.")]
    public void DeprecationMessage_ShouldNameOnlyMembersThatExist_WhenItSaysWhatToReadInstead(string deprecated)
    {
        // Arrange
        var message = Vsdk0004Messages().Single(m => m.Name == deprecated).Message;
        var assembly = typeof(IChanWebSocketSession).Assembly;

        // Act — every SDK type, Type.Member and session method the message names
        var missing = new List<string>();
        foreach (Match m in SdkTypeOrMember().Matches(message))
        {
            var type = assembly.GetType($"{typeof(IChanWebSocketSession).Namespace}.{m.Groups["type"].Value}");
            if (type is null)
                missing.Add(m.Value);
            else if (m.Groups["member"].Success && type.GetMember(m.Groups["member"].Value).Length == 0)
                missing.Add(m.Value);
        }

        missing.AddRange(AsyncMethodName().Matches(message)
            .Select(m => m.Value)
            .Where(name => typeof(IChanWebSocketSession).GetMethod(name, [typeof(CancellationToken)]) is null));

        // Assert
        missing.Should().BeEmpty("a deprecation that names what to read instead names something that exists");
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test code: enumerates the Ari assembly's own types, never trimmed here.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test code: reads the public members of the Ari assembly's own types, never trimmed here.")]
    private static List<(string Name, string Message)> Vsdk0004Messages()
    {
        var messages = new List<(string Name, string Message)>();
        var assembly = typeof(IChanWebSocketSession).Assembly;
        foreach (var type in assembly.GetExportedTypes().Where(t => t.Namespace == typeof(IChanWebSocketSession).Namespace))
        {
            if (type.GetCustomAttribute<ObsoleteAttribute>() is { DiagnosticId: DeprecationId } onType)
                messages.Add((type.Name, onType.Message!));

            messages.AddRange(type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(member => (Member: member, Obsolete: member.GetCustomAttribute<ObsoleteAttribute>()))
                .Where(x => x.Obsolete is { DiagnosticId: DeprecationId })
                .Select(x => ($"{type.Name}.{x.Member.Name}", x.Obsolete!.Message!)));
        }

        return messages;
    }

    [GeneratedRegex(@"\b(?<type>ChanWebSocket\w+)(?:\.(?<member>[A-Z]\w*))?", RegexOptions.CultureInvariant)]
    private static partial Regex SdkTypeOrMember();

    [GeneratedRegex(@"\b\w+Async\b", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncMethodName();

    [Fact]
    public void DtmfDurationMs_ShouldBeObsoleteWithVsdk0004NamingThePeersDtmfEvents_WhenAConsumerReadsIt()
    {
        // Arrange
#pragma warning disable VSDK0004 // the obsolete read is the point
        var property = typeof(ChanWebSocketDtmf).GetProperty(nameof(ChanWebSocketDtmf.DurationMs));
#pragma warning restore VSDK0004
        property.Should().NotBeNull("the property stays; deprecating it removes nothing");

        // Act
        var obsolete = property!.GetCustomAttribute<ObsoleteAttribute>();

        // Assert
        obsolete.Should().NotBeNull("DTMF_END carries digit and channel_id only, so the duration is always 0");
        obsolete!.DiagnosticId.Should().Be(DeprecationId);
        obsolete.IsError.Should().BeFalse();
        obsolete.Message.Should().Contain("0")
            .And.Contain("ChannelDtmfReceived")
            .And.Contain("DTMFEnd")
            .And.Contain("peer");
    }

    [Fact]
    public void DtmfConstructorAndDeconstruct_ShouldCarryNoObsolete_WhenOnlyThePropertyIsDeprecated()
    {
        // Arrange — the constructor and Deconstruct stay exactly as shipped: only a read of DurationMs warns
        var constructor = typeof(ChanWebSocketDtmf).GetConstructor([typeof(string), typeof(int)]);
        var deconstruct = typeof(ChanWebSocketDtmf).GetMethod("Deconstruct");

        // Assert
        constructor.Should().NotBeNull();
        deconstruct.Should().NotBeNull();
        constructor!.GetCustomAttribute<ObsoleteAttribute>().Should().BeNull();
        constructor.GetParameters().Should().OnlyContain(p => p.GetCustomAttribute<ObsoleteAttribute>() == null);
        deconstruct!.GetCustomAttribute<ObsoleteAttribute>().Should().BeNull();
    }

    [Fact]
    public void MediaStart_ShouldReadNull_WhenAnImplementationHasOnlyThe280Members()
    {
        // Arrange — an implementation written against 2.8.0 compiles unchanged: MediaStart is a default member
        IChanWebSocketSession session = new SessionWrittenAgainst280();

        // Act
        var mediaStart = session.MediaStart;

        // Assert — the default never reports a MEDIA_START
        mediaStart.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(IChanWebSocketSession.SendFlushMediaAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendPauseMediaAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendContinueMediaAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendGetStatusAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendStartMediaBufferingAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendStopMediaBufferingAsync))]
    [InlineData(nameof(IChanWebSocketSession.SendReportQueueDrainedAsync))]
    public void AddedCommands_ShouldThrowNotSupportedExceptionNamingTheMethod_WhenAnImplementationHasOnlyThe280Members(string method)
    {
        // Arrange — an implementation written against 2.8.0 compiles unchanged: every added command is a default member
        IChanWebSocketSession session = new SessionWrittenAgainst280();
        Func<ValueTask> call = method switch
        {
            nameof(IChanWebSocketSession.SendFlushMediaAsync) => () => session.SendFlushMediaAsync(),
            nameof(IChanWebSocketSession.SendPauseMediaAsync) => () => session.SendPauseMediaAsync(),
            nameof(IChanWebSocketSession.SendContinueMediaAsync) => () => session.SendContinueMediaAsync(),
            nameof(IChanWebSocketSession.SendGetStatusAsync) => () => session.SendGetStatusAsync(),
            nameof(IChanWebSocketSession.SendStartMediaBufferingAsync) => () => session.SendStartMediaBufferingAsync(),
            nameof(IChanWebSocketSession.SendStopMediaBufferingAsync) => () => session.SendStopMediaBufferingAsync("b4-0"),
            _ => () => session.SendReportQueueDrainedAsync(),
        };

        // Act — never awaited: the default body throws before any ValueTask exists, so nothing can be written
#pragma warning disable CA2012 // the discarded ValueTask is never created: the throw is the point
        var act = () => { _ = call(); };
#pragma warning restore CA2012

        // Assert
        act.Should().Throw<NotSupportedException>().Which.Message.Should().Contain(method);
    }

    /// <summary>Exactly the members <see cref="IChanWebSocketSession"/> and <see cref="IAudioStream"/> had in 2.8.0.</summary>
    private sealed class SessionWrittenAgainst280 : IChanWebSocketSession
    {
        public string ChannelId => "c";

        public string Format => "slin16";

        public int SampleRate => 16000;

        public bool IsConnected => false;

        public IObservable<AudioStreamState> StateChanges => throw new NotSupportedException();

        public IObservable<ChanWebSocketControlMessage> ControlMessages => throw new NotSupportedException();

        public ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask WriteFrameAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask SendMarkAsync(string mark, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask SendXonAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask SendXoffAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask SendSetMediaDirectionAsync(
            ChanWebSocketMediaDirection direction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
