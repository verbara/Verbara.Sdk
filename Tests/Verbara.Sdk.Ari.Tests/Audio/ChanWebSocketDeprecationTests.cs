using System.Reflection;
using FluentAssertions;
using Verbara.Sdk.Ari.Audio;

namespace Verbara.Sdk.Ari.Tests.Audio;

/// <summary>
/// The chan_websocket control surface that says something Asterisk never sends or acts on is deprecated with one id,
/// <c>VSDK0004</c>, and nothing is removed. Reading the attribute is the point of each fact: a consumer building with
/// <c>TreatWarningsAsErrors</c> gets <c>VSDK0004</c>, not a blanket <c>CS0618</c>, and the message says what to read
/// instead.
/// </summary>
public class ChanWebSocketDeprecationTests
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
    public void ChanWebSocketMediaBuffering_ShouldBeObsoleteWithVsdk0004NamingXoffAndXon_WhenAConsumerNamesIt()
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
            .And.Contain(nameof(ChanWebSocketMediaXon));
    }

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
