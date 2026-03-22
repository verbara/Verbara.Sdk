using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using NSubstitute;
using PbxAdmin.Models;
using PbxAdmin.Services;

namespace PbxAdmin.Tests.Services;

/// <summary>
/// Tests for SoftphoneService state machine and JS callback handling.
/// JS callbacks (OnRegistered, OnIncomingCall, …) are invoked directly — no JS interop needed.
/// ConnectAsync is exercised separately via integration tests because it requires a live
/// provisioning provider; all state transitions reachable via callbacks are fully covered here.
/// </summary>
public class SoftphoneServiceTests
{
    private static SoftphoneService CreateSut(out IToastService toast)
    {
        var js = Substitute.For<IJSRuntime>();
        toast = Substitute.For<IToastService>();

        // WebRtcProviderResolver is a concrete sealed class whose providers are also sealed,
        // so NSubstitute cannot proxy them. Because none of the callback-only tests invoke
        // ConnectAsync, the providers are never called — passing null is safe here.
        var configResolver = Substitute.For<IConfigProviderResolver>();
        configResolver.GetConfigMode(Arg.Any<string>()).Returns(ConfigMode.File);

        var resolver = new WebRtcProviderResolver(null!, null!, configResolver);

        return new SoftphoneService(js, toast, resolver);
    }

    // -----------------------------------------------------------------------
    // Initial state
    // -----------------------------------------------------------------------

    [Fact]
    public void InitialState_ShouldBeUnregistered()
    {
        var sut = CreateSut(out _);

        sut.State.Should().Be(SoftphoneState.Unregistered);
        sut.Extension.Should().BeNull();
        sut.RemoteNumber.Should().BeNull();
        sut.RemoteName.Should().BeNull();
        sut.IsMuted.Should().BeFalse();
        sut.IsOnHold.Should().BeFalse();
        sut.CallStartedAt.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Registration callbacks
    // -----------------------------------------------------------------------

    [Fact]
    public void OnRegistered_ShouldSetStateToIdle()
    {
        var sut = CreateSut(out var toast);

        sut.OnRegistered();

        sut.State.Should().Be(SoftphoneState.Idle);
        toast.Received(1).Show(Arg.Any<string>(), ToastLevel.Success, Arg.Any<string?>());
    }

    [Fact]
    public void OnRegistrationFailed_ShouldResetToUnregistered()
    {
        var sut = CreateSut(out var toast);

        sut.OnRegistrationFailed("503 Service Unavailable");

        sut.State.Should().Be(SoftphoneState.Unregistered);
        toast.Received(1).Show(Arg.Any<string>(), ToastLevel.Error, "503 Service Unavailable");
    }

    [Fact]
    public void OnRingingOut_ShouldSetStateToRingingOut()
    {
        var sut = CreateSut(out _);

        sut.OnRingingOut();

        sut.State.Should().Be(SoftphoneState.RingingOut);
    }

    // -----------------------------------------------------------------------
    // Incoming call callbacks
    // -----------------------------------------------------------------------

    [Fact]
    public void OnIncomingCall_ShouldSetStateToRingingIn_WithCallerInfo()
    {
        var sut = CreateSut(out _);

        sut.OnIncomingCall("Alice", "1001");

        sut.State.Should().Be(SoftphoneState.RingingIn);
        sut.RemoteName.Should().Be("Alice");
        sut.RemoteNumber.Should().Be("1001");
    }

    [Fact]
    public void OnIncomingCall_EmptyName_ShouldSetRemoteNameToNull()
    {
        var sut = CreateSut(out _);

        sut.OnIncomingCall("", "1002");

        sut.RemoteName.Should().BeNull();
        sut.RemoteNumber.Should().Be("1002");
    }

    // -----------------------------------------------------------------------
    // In-call callbacks
    // -----------------------------------------------------------------------

    [Fact]
    public void OnCallAnswered_ShouldSetStateToInCall_WithTimestamp()
    {
        var sut = CreateSut(out _);

        sut.OnCallAnswered();

        sut.State.Should().Be(SoftphoneState.InCall);
        sut.CallStartedAt.Should().NotBeNull();
        sut.IsMuted.Should().BeFalse();
        sut.IsOnHold.Should().BeFalse();
    }

    [Fact]
    public void OnCallEnded_ShouldResetToIdle()
    {
        var sut = CreateSut(out var toast);
        sut.OnIncomingCall("Bob", "2001");
        sut.OnCallAnswered();

        sut.OnCallEnded();

        sut.State.Should().Be(SoftphoneState.Idle);
        sut.CallStartedAt.Should().BeNull();
        sut.RemoteNumber.Should().BeNull();
        sut.RemoteName.Should().BeNull();
        sut.IsMuted.Should().BeFalse();
        sut.IsOnHold.Should().BeFalse();
        toast.Received(1).Show(Arg.Any<string>(), ToastLevel.Info, Arg.Any<string?>());
    }

    [Fact]
    public void OnCallFailed_ShouldResetToIdle()
    {
        var sut = CreateSut(out var toast);

        sut.OnCallFailed("No answer");

        sut.State.Should().Be(SoftphoneState.Idle);
        toast.Received(1).Show(Arg.Any<string>(), ToastLevel.Error, "No answer");
    }

    // -----------------------------------------------------------------------
    // Hold / mute callbacks
    // -----------------------------------------------------------------------

    [Fact]
    public void OnHoldChanged_True_ShouldSetOnHoldState()
    {
        var sut = CreateSut(out _);
        sut.OnCallAnswered();

        sut.OnHoldChanged(true);

        sut.State.Should().Be(SoftphoneState.OnHold);
        sut.IsOnHold.Should().BeTrue();
    }

    [Fact]
    public void OnHoldChanged_False_ShouldRestoreInCall()
    {
        var sut = CreateSut(out _);
        sut.OnCallAnswered();
        sut.OnHoldChanged(true);

        sut.OnHoldChanged(false);

        sut.State.Should().Be(SoftphoneState.InCall);
        sut.IsOnHold.Should().BeFalse();
    }

    [Fact]
    public void OnMuteChanged_ShouldUpdateMuteState()
    {
        var sut = CreateSut(out _);

        sut.OnMuteChanged(true);
        sut.IsMuted.Should().BeTrue();

        sut.OnMuteChanged(false);
        sut.IsMuted.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // OnStateChanged event
    // -----------------------------------------------------------------------

    [Fact]
    public void OnRegistered_ShouldFireOnStateChanged()
    {
        var sut = CreateSut(out _);
        var fired = false;
        sut.OnStateChanged += () => fired = true;

        sut.OnRegistered();

        fired.Should().BeTrue();
    }

    [Fact]
    public void OnMuteChanged_ShouldFireOnStateChanged()
    {
        var sut = CreateSut(out _);
        var fired = false;
        sut.OnStateChanged += () => fired = true;

        sut.OnMuteChanged(true);

        fired.Should().BeTrue();
    }
}
