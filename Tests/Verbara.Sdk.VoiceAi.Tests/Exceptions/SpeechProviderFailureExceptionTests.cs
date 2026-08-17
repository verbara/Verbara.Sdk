using System.Net;
using System.Net.WebSockets;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tests.Exceptions;

/// <summary>
/// Tests for the four factories on <see cref="SpeechProviderFailureException"/> — one per channel in
/// <see cref="SpeechProviderFailureSignal"/> — and for the close-code rule they share.
/// </summary>
/// <remarks>
/// <para>
/// These factories are also exercised end to end through all eight WebSocket clients, so a reader may
/// reasonably ask what this class adds. Two things.
/// </para>
/// <para>
/// First, <see cref="SpeechProviderFailureException.FromCloseStatus"/> is a <em>shared rule</em>: the
/// close-code door was open at all eight surfaces and the fix put the decision in one place precisely
/// so eight copies could not drift apart. A rule that decides on behalf of eight callers earns a
/// direct test of its own boundaries — which codes mean "ended" and which mean "failed" — rather than
/// only being observed through whichever code one vendor's fake happens to send.
/// </para>
/// <para>
/// Second, the end-to-end coverage of this type is not measurable where it happens. Measured
/// 2026-08-17: <c>Verbara.Sdk.VoiceAi.Stt.Tests</c> and <c>Verbara.Sdk.VoiceAi.Tts.Tests</c> do not
/// instrument the <c>Verbara.Sdk.VoiceAi</c> assembly at all — coverlet reports
/// <c>Unable to instrument module: …/Verbara.Sdk.VoiceAi.dll</c> in those runs (and for
/// <c>Verbara.Sdk.VoiceAi.AudioSocket.dll</c> with it), so every hit those suites land on this type is
/// dropped on the floor. It is pre-existing infrastructure debt, reproducible on a single-project run
/// and independent of this change. Until it is fixed, this assembly's behaviour is only *visible* when
/// asserted from a suite that does instrument it, and this is one.
/// </para>
/// </remarks>
public sealed class SpeechProviderFailureExceptionTests
{
    private const string Provider = "TestVendor";

    [Fact]
    public void FromErrorFrame_ShouldCarryTheVendorCodeAndMessage_WhenTheFrameHasBoth()
    {
        var failure = SpeechProviderFailureException.FromErrorFrame(
            Provider, "invalid_api_key", "The API key you supplied is not valid");

        failure.Provider.Should().Be(Provider);
        failure.Signal.Should().Be(SpeechProviderFailureSignal.ErrorFrame);
        failure.Code.Should().Be("invalid_api_key", "the vendor's code is carried verbatim and unparsed");
        failure.Message.Should().Contain("invalid_api_key").And.Contain("The API key you supplied is not valid");
        failure.InnerException.Should().BeNull("an in-band failure wraps no transport exception");
    }

    [Fact]
    public void FromErrorFrame_ShouldOmitTheParentheses_WhenTheVendorGaveNoCode()
    {
        // Measured shapes differ: some vendors send a code, some only prose. The message must read
        // correctly either way rather than printing an empty pair of parentheses.
        var failure = SpeechProviderFailureException.FromErrorFrame(Provider, code: null, "something broke");

        failure.Code.Should().BeNull();
        failure.Message.Should().Be($"{Provider} reported a failure: something broke");
    }

    [Fact]
    public void FromErrorFrame_ShouldSayNoMessage_WhenTheFrameCarriedOnlyACode()
    {
        var failure = SpeechProviderFailureException.FromErrorFrame(Provider, "3007", vendorMessage: null);

        failure.Code.Should().Be("3007");
        failure.Message.Should().Be($"{Provider} reported a failure (3007): (no message)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(WebSocketCloseStatus.NormalClosure)]
    [InlineData(WebSocketCloseStatus.Empty)]
    public void FromCloseStatus_ShouldReturnNull_WhenTheCodeMeansTheSessionEnded(WebSocketCloseStatus? closeStatus)
    {
        // The three non-failures, stated as data: no close status at all, a normal closure, and a
        // close carrying no code. Everything else is a failure — asserted by the theory below.
        SpeechProviderFailureException
            .FromCloseStatus(Provider, closeStatus, closeStatusDescription: null)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(WebSocketCloseStatus.PolicyViolation, "1008")]
    [InlineData(WebSocketCloseStatus.EndpointUnavailable, "1001")]
    [InlineData(WebSocketCloseStatus.ProtocolError, "1002")]
    [InlineData(WebSocketCloseStatus.InternalServerError, "1011")]
    public void FromCloseStatus_ShouldReportAFailure_WhenTheCodeIsAnythingElse(
        WebSocketCloseStatus closeStatus,
        string expectedCode)
    {
        var failure = SpeechProviderFailureException.FromCloseStatus(Provider, closeStatus, "not_authorised");

        failure.Should().NotBeNull();
        failure!.Signal.Should().Be(SpeechProviderFailureSignal.CloseCode);
        failure.Code.Should().Be(expectedCode, "the close code is reported as its number, not its .NET name");
        failure.Message.Should().Contain(expectedCode).And.Contain("not_authorised");
    }

    [Fact]
    public void FromCloseStatus_ShouldReportTheVendorRange_WhenTheCodeIsOutsideThe1xxxCodes()
    {
        // Vendors define their own 3xxx/4xxx codes and .NET has no enum member for them, so the cast
        // path — not a named member — is what production hits on those surfaces.
        var failure = SpeechProviderFailureException.FromCloseStatus(
            Provider, (WebSocketCloseStatus)4001, "not_authorised");

        failure.Should().NotBeNull();
        failure!.Code.Should().Be("4001");
    }

    [Fact]
    public void FromCloseStatus_ShouldSayNoReasonGiven_WhenTheCloseCarriedNoDescription()
    {
        var failure = SpeechProviderFailureException.FromCloseStatus(
            Provider, WebSocketCloseStatus.PolicyViolation, closeStatusDescription: "");

        failure.Should().NotBeNull();
        failure!.Message.Should().Be($"{Provider} closed the session with code 1008: no reason given");
    }

    [Fact]
    public void FromHandshake_ShouldReportTheHttpStatus_WhenTheUpgradeWasRejectedWithOne()
    {
        var transport = new WebSocketException(WebSocketError.NotAWebSocket);

        var failure = SpeechProviderFailureException.FromHandshake(
            Provider, HttpStatusCode.Unauthorized, transport);

        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Code.Should().Be("401");
        failure.Message.Should().Contain("401").And.Contain("no session opened");
        failure.InnerException.Should().BeSameAs(transport, "the transport exception is never discarded");
    }

    [Theory]
    [InlineData(null)]
    [InlineData((HttpStatusCode)0)]
    public void FromHandshake_ShouldReportNoCode_WhenTheUpgradeFailedWithoutAnHttpAnswer(HttpStatusCode? status)
    {
        // A refused connection, an unresolvable name, a TLS failure: there is no status to report, and
        // zero is what ClientWebSocket leaves behind in that case. Reporting "0" would be a made-up
        // vendor code, so both cases must read as no code at all.
        var failure = SpeechProviderFailureException.FromHandshake(
            Provider, status, new WebSocketException(WebSocketError.Faulted));

        failure.Signal.Should().Be(SpeechProviderFailureSignal.Handshake);
        failure.Code.Should().BeNull();
        failure.Message.Should().Be($"{Provider}: the connection upgrade failed and no session opened.");
    }

    [Fact]
    public void FromTransport_ShouldReportNoCodeAndKeepTheInnerException_WhenTheConnectionDiedMidSession()
    {
        var transport = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);

        var failure = SpeechProviderFailureException.FromTransport(Provider, transport);

        failure.Signal.Should().Be(SpeechProviderFailureSignal.Transport);
        failure.Code.Should().BeNull("a dead socket says nothing, so there is no vendor code to carry");
        failure.Message.Should().Contain("the result is incomplete");
        failure.InnerException.Should().BeSameAs(transport, "the inner exception is the only evidence there is");
    }

    [Fact]
    public void FromTransport_ShouldBeCatchableAsTheBaseType_WhenACallerDoesNotCareWhichChannelFailed()
    {
        // ADR-0050 E4: two types, and a caller that only wants "the provider failed" catches the base.
        // Asserted here because it is a promise to callers, not an implementation detail.
        SpeechProviderFailureException.FromTransport(Provider, new WebSocketException())
            .Should().BeAssignableTo<SpeechProviderException>();
    }
}
