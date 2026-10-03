using Verbara.Sdk.VoiceAi.OpenAiRealtime.FunctionCalling;
using Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.FunctionCalling;

/// <summary>
/// <c>OpenAiRealtimeOptions.FunctionCallTimeout</c>: greater than zero and no longer than <c>int.MaxValue</c>
/// milliseconds, rejected otherwise by the options validator naming the option, by the bridge's constructor, and
/// again before each function call, since the bridge holds its options object by reference and
/// <see cref="Timeout.InfiniteTimeSpan"/> set after construction would otherwise switch the bound off silently.
/// </summary>
public sealed partial class FunctionCallTimeoutTests
{
    private const string Option = "FunctionCallTimeout";

    public static TheoryData<TimeSpan> UnusableTimeouts =>
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(-1),
        Timeout.InfiniteTimeSpan,
        TimeSpan.FromMilliseconds(int.MaxValue) + TimeSpan.FromTicks(1),
    ];

    public static TheoryData<TimeSpan> UsableTimeouts =>
    [
        TimeSpan.FromTicks(1),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMilliseconds(int.MaxValue),
    ];

    public static TheoryData<TimeSpan> UnusableAtConstruction =>
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(-1),
        Timeout.InfiniteTimeSpan,
    ];

    [Fact]
    public void FunctionCallTimeout_ShouldDefaultToThirtySeconds()
    {
        new OpenAiRealtimeOptions().FunctionCallTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [MemberData(nameof(UnusableTimeouts))]
    public void Validate_ShouldFailNamingTheOption_WhenFunctionCallTimeoutIsUnusable(TimeSpan value)
    {
        var options = SessionRun.DefaultOptions();
        options.FunctionCallTimeout = value;

        var result = new OpenAiRealtimeOptionsValidator().Validate(null, options);

        using (new AssertionScope())
        {
            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain(Option);
        }
    }

    [Theory]
    [MemberData(nameof(UsableTimeouts))]
    public void Validate_ShouldSucceed_WhenFunctionCallTimeoutIsUsable(TimeSpan value)
    {
        var options = SessionRun.DefaultOptions();
        options.FunctionCallTimeout = value;

        new OpenAiRealtimeOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(UnusableAtConstruction))]
    public async Task Ctor_ShouldThrowNamingTheOption_WhenFunctionCallTimeoutIsUnusable(TimeSpan value)
    {
        var options = SessionRun.DefaultOptions();
        options.FunctionCallTimeout = value;
        OpenAiRealtimeBridge? created = null;

        var fault = Record.Exception(() => created = new OpenAiRealtimeBridge(
            Options.Create(options), new RealtimeFunctionRegistry([]), NullLogger<OpenAiRealtimeBridge>.Instance));
        if (created is not null)
            await created.DisposeAsync();

        fault.Should().BeOfType<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(Option);
    }

    [Theory]
    [MemberData(nameof(UnusableAtConstruction))]
    public async Task HandleSessionAsync_ShouldThrowNamingTheOptionBeforeTheCall_WhenFunctionCallTimeoutWasMadeUnusableAfterConstruction(TimeSpan value)
    {
        using var function = ScriptedFunction.ReturnsWhenReleased();
        var options = SessionRun.DefaultOptions();
        await using var run = await SessionRun.StartAsync(function, options: options, requestTheCall: false);

        options.FunctionCallTimeout = value;
        await run.Fake.SendEventAsync(ScriptedFunction.CallEvent);
        var fault = await Record.ExceptionAsync(() => run.SessionTask.WaitAsync(SignalTimeout));

        using (new AssertionScope())
        {
            fault.Should().BeOfType<ArgumentOutOfRangeException>("the value is checked again before each call")
                .Which.ParamName.Should().Be(Option);
            function.Started.IsCompleted.Should().BeFalse("the handler is not called with an unusable bound");
            run.ItemFrames().Should().BeEmpty("nothing is sent to the vendor for the call");
        }
    }
}
