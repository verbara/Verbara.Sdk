using Verbara.Sdk.Ari.Client;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Resilience;

namespace Verbara.Sdk.Ari.Tests.Client;

public sealed class AriClientOptionsValidatorTests
{
    private readonly AriClientOptionsValidator _sut = new();

    [Fact]
    public void Validate_ShouldSucceed_WhenAllFieldsAreValid()
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "admin",
            Password = "secret",
            Application = "myapp"
        };

        var result = _sut.Validate(null, options);

        result.Should().Be(ValidateOptionsResult.Success);
    }

    [Fact]
    public void Validate_ShouldFail_WhenUsernameIsEmpty()
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "",
            Password = "secret",
            Application = "myapp"
        };

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenPasswordIsEmpty()
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "admin",
            Password = "",
            Application = "myapp"
        };

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenApplicationIsEmpty()
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "admin",
            Password = "secret",
            Application = ""
        };

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenMaxReconnectAttemptsIsNegativeAndAutoReconnectIsOn()
    {
        var options = ReconnectOptions("MaxReconnectAttempts = -1", autoReconnect: true);

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeFalse("with AutoReconnect on, a negative attempt limit is rejected as the AMI validator rejects it");
        result.FailureMessage.Should().Contain(nameof(AriClientOptions.MaxReconnectAttempts), "the failure names the option to fix");
    }

    /// <summary>
    /// Owner ruling Q1 (2026-09-30): ARI's attempt-limit check applies only with <c>AutoReconnect</c> on, like the rest of
    /// the reconnect rule, so a host with reconnection off and a negative limit keeps validating as it does today.
    /// </summary>
    [Fact]
    public void Validate_ShouldSucceed_WhenMaxReconnectAttemptsIsNegativeAndAutoReconnectIsOff()
    {
        var options = ReconnectOptions("MaxReconnectAttempts = -1", autoReconnect: false);

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue(
            $"without AutoReconnect the attempt limit is never used, so it is not checked; failure: {result.FailureMessage}");
    }

    /// <summary>
    /// The reconnect values the backoff cannot use, each with the option it names. With <c>AutoReconnect</c> on, the
    /// validator rejects each one naming that option; they are what <see cref="BackoffSchedule.Compute"/> rejects, plus a
    /// delay above what .NET can wait (<see cref="int.MaxValue"/> ms, about 24.8 days; owner ruling Q2, 2026-09-30).
    /// </summary>
    public static TheoryData<string, string> UnusableReconnectValues => new()
    {
        { "ReconnectMultiplier = 0.5", nameof(AriClientOptions.ReconnectMultiplier) },
        { "ReconnectMultiplier = 0", nameof(AriClientOptions.ReconnectMultiplier) },
        { "ReconnectMultiplier = -1", nameof(AriClientOptions.ReconnectMultiplier) },
        { "ReconnectMultiplier = NaN", nameof(AriClientOptions.ReconnectMultiplier) },
        { "ReconnectMultiplier = +Infinity", nameof(AriClientOptions.ReconnectMultiplier) },
        { "ReconnectMaxDelay = 500 ms < ReconnectInitialDelay = 1 s", nameof(AriClientOptions.ReconnectMaxDelay) },
        { "ReconnectInitialDelay = -1 s", nameof(AriClientOptions.ReconnectInitialDelay) },
        { "ReconnectMaxDelay = Timeout.InfiniteTimeSpan", nameof(AriClientOptions.ReconnectMaxDelay) },
        { "ReconnectMaxDelay = 60 days, above the int.MaxValue ms wait limit", nameof(AriClientOptions.ReconnectMaxDelay) },
    };

    /// <summary>
    /// What a constructor rejects with <c>AutoReconnect</c> on: every value of <see cref="UnusableReconnectValues"/>, and a
    /// negative attempt limit, which the validator also rejects with <c>AutoReconnect</c> on and a constructor must check itself.
    /// </summary>
    public static TheoryData<string, string> ValuesAConstructorRejects
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var row in (IEnumerable<object[]>)UnusableReconnectValues)
                data.Add((string)row[0], (string)row[1]);
            data.Add("MaxReconnectAttempts = -1", nameof(AriClientOptions.MaxReconnectAttempts));
            return data;
        }
    }

    /// <summary>The boundary values the backoff accepts: a multiplier of exactly 1, a maximum equal to the initial delay, a zero initial delay, a maximum of exactly the wait limit.</summary>
    public static TheoryData<string> BoundaryReconnectValues => new()
    {
        "ReconnectMultiplier = 1",
        "ReconnectMaxDelay = ReconnectInitialDelay = 1 s",
        "ReconnectInitialDelay = 0",
        "ReconnectInitialDelay = 0, ReconnectMultiplier = 1, ReconnectMaxDelay = 0",
        "ReconnectMaxDelay = int.MaxValue ms",
    };

    [Theory]
    [MemberData(nameof(UnusableReconnectValues))]
    public void Validate_ShouldFailNamingTheOption_WhenAReconnectValueIsUnusableAndAutoReconnectIsOn(string value, string option)
    {
        var options = ReconnectOptions(value, autoReconnect: true);

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeFalse($"with AutoReconnect on, {value} is a value the reconnect backoff cannot use");
        result.FailureMessage.Should().Contain(option, "the failure names the option to fix");
    }

    [Theory]
    [MemberData(nameof(BoundaryReconnectValues))]
    public void Validate_ShouldSucceed_WhenTheBoundaryValuesAreUsed(string value)
    {
        var options = ReconnectOptions(value, autoReconnect: true);

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue($"{value} is a value the reconnect backoff accepts; failure: {result.FailureMessage}");
    }

    [Theory]
    [MemberData(nameof(UnusableReconnectValues))]
    public void Validate_ShouldSucceed_WhenAutoReconnectIsOffWithUnusableValues(string value, string option)
    {
        var options = ReconnectOptions(value, autoReconnect: false);

        var result = _sut.Validate(null, options);

        result.Succeeded.Should().BeTrue(
            $"without AutoReconnect the backoff never runs, so {value} ({option}) is not checked; failure: {result.FailureMessage}");
    }

    [Fact]
    public void Validate_ShouldAgreeWithTheBackoffScheduleAndTheWaitLimit_OverTheBoundarySet()
    {
        IEnumerable<object[]> unusable = UnusableReconnectValues;
        IEnumerable<object[]> boundary = BoundaryReconnectValues;
        var values = unusable.Concat(boundary).Select(row => (string)row[0]);

        using (new AssertionScope())
        {
            foreach (var value in values)
            {
                var options = ReconnectOptions(value, autoReconnect: true);
                var validatorAccepts = _sut.Validate(null, options).Succeeded;

                validatorAccepts.Should().Be(BackoffAccepts(options) && WithinWaitLimit(options),
                    $"the validator accepts {value} if and only if BackoffSchedule.Compute does and no delay exceeds the wait limit");
            }
        }
    }

    /// <summary>The longest delay .NET can wait: <see cref="int.MaxValue"/> ms, about 24.8 days (owner ruling Q2).</summary>
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromMilliseconds(int.MaxValue);

    private static bool WithinWaitLimit(AriClientOptions options) =>
        options.ReconnectInitialDelay <= WaitLimit && options.ReconnectMaxDelay <= WaitLimit;

    private static bool BackoffAccepts(AriClientOptions options)
    {
        try
        {
            _ = BackoffSchedule.Compute(1, options.ReconnectInitialDelay, options.ReconnectMultiplier, options.ReconnectMaxDelay);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Valid options with <paramref name="autoReconnect"/>, changed by the one value named.</summary>
    internal static AriClientOptions ReconnectOptions(string value, bool autoReconnect)
    {
        var options = new AriClientOptions
        {
            BaseUrl = "http://localhost:8088",
            Username = "admin",
            Password = "secret",
            Application = "myapp",
            AutoReconnect = autoReconnect,
        };

        switch (value)
        {
            case "ReconnectMultiplier = 0.5": options.ReconnectMultiplier = 0.5; break;
            case "ReconnectMultiplier = 0": options.ReconnectMultiplier = 0; break;
            case "ReconnectMultiplier = -1": options.ReconnectMultiplier = -1; break;
            case "ReconnectMultiplier = NaN": options.ReconnectMultiplier = double.NaN; break;
            case "ReconnectMultiplier = +Infinity": options.ReconnectMultiplier = double.PositiveInfinity; break;
            case "ReconnectMaxDelay = 500 ms < ReconnectInitialDelay = 1 s":
                options.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
                options.ReconnectMaxDelay = TimeSpan.FromMilliseconds(500);
                break;
            case "ReconnectInitialDelay = -1 s": options.ReconnectInitialDelay = TimeSpan.FromSeconds(-1); break;
            case "ReconnectMaxDelay = Timeout.InfiniteTimeSpan": options.ReconnectMaxDelay = Timeout.InfiniteTimeSpan; break;
            case "ReconnectMaxDelay = 60 days, above the int.MaxValue ms wait limit": options.ReconnectMaxDelay = TimeSpan.FromDays(60); break;
            case "ReconnectMaxDelay = int.MaxValue ms": options.ReconnectMaxDelay = WaitLimit; break;
            case "MaxReconnectAttempts = -1": options.MaxReconnectAttempts = -1; break;
            case "ReconnectMultiplier = 1": options.ReconnectMultiplier = 1.0; break;
            case "ReconnectMaxDelay = ReconnectInitialDelay = 1 s":
                options.ReconnectInitialDelay = TimeSpan.FromSeconds(1);
                options.ReconnectMaxDelay = TimeSpan.FromSeconds(1);
                break;
            case "ReconnectInitialDelay = 0": options.ReconnectInitialDelay = TimeSpan.Zero; break;
            case "ReconnectInitialDelay = 0, ReconnectMultiplier = 1, ReconnectMaxDelay = 0":
                options.ReconnectInitialDelay = TimeSpan.Zero;
                options.ReconnectMultiplier = 1.0;
                options.ReconnectMaxDelay = TimeSpan.Zero;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(value), value, "Not a reconnect value of these tests.");
        }

        return options;
    }
}
