using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ari.Client;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;
using Verbara.Sdk.Resilience;

namespace Verbara.Sdk.Ami.Tests.Connection;

public class AmiConnectionOptionsValidatorTests
{
    private readonly AmiConnectionOptionsValidator _validator = new();

    [Fact]
    public void Validate_ShouldSucceed_WithValidOptions()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Port = 5038,
            Username = "admin",
            Password = "secret"
        };

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenUsernameEmpty()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "",
            Password = "secret"
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenPasswordEmpty()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Username = "admin",
            Password = ""
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenPortOutOfRange()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Port = 0,
            Username = "admin",
            Password = "secret"
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_ShouldFail_WhenHostnameNull()
    {
        var options = new AmiConnectionOptions
        {
            Hostname = null!,
            Username = "admin",
            Password = "secret"
        };

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void AriValidator_ShouldFail_WhenBaseUrlEmpty()
    {
        var ariValidator = new AriClientOptionsValidator();
        var options = new AriClientOptions
        {
            BaseUrl = "",
            Username = "admin",
            Password = "secret",
            Application = "testapp"
        };

        var result = ariValidator.Validate(null, options);

        result.Failed.Should().BeTrue();
    }

    /// <summary>
    /// The recorded defaults: a heartbeat every 30 s with a 10 s timeout, enabled (ADR-0021), and a
    /// reconnect that starts at 1 s, doubles, stops growing at 30 s and never gives up (ADR-0008).
    /// </summary>
    [Fact]
    public void Constructor_ShouldUseTheRecordedHeartbeatAndReconnectDefaults_WhenNothingIsSet()
    {
        var options = new AmiConnectionOptions();

        options.Should().BeEquivalentTo(new
        {
            EnableHeartbeat = true,
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            HeartbeatTimeout = TimeSpan.FromSeconds(10),
            AutoReconnect = true,
            ReconnectInitialDelay = TimeSpan.FromSeconds(1),
            ReconnectMultiplier = 2.0,
            ReconnectMaxDelay = TimeSpan.FromSeconds(30),
            MaxReconnectAttempts = 0,
        });
    }

    /// <summary>
    /// The reconnect values the backoff cannot use, each with the option it names. With <c>AutoReconnect</c> on, the
    /// validator rejects each one naming that option; they are what <see cref="BackoffSchedule.Compute"/> rejects, plus a
    /// delay above what .NET can wait (<see cref="int.MaxValue"/> ms, about 24.8 days; owner ruling Q2, 2026-09-30).
    /// </summary>
    public static TheoryData<string, string> UnusableReconnectValues => new()
    {
        { "ReconnectMultiplier = 0.5", nameof(AmiConnectionOptions.ReconnectMultiplier) },
        { "ReconnectMultiplier = 0", nameof(AmiConnectionOptions.ReconnectMultiplier) },
        { "ReconnectMultiplier = -1", nameof(AmiConnectionOptions.ReconnectMultiplier) },
        { "ReconnectMultiplier = NaN", nameof(AmiConnectionOptions.ReconnectMultiplier) },
        { "ReconnectMultiplier = +Infinity", nameof(AmiConnectionOptions.ReconnectMultiplier) },
        { "ReconnectMaxDelay = 500 ms < ReconnectInitialDelay = 1 s", nameof(AmiConnectionOptions.ReconnectMaxDelay) },
        { "ReconnectInitialDelay = -1 s", nameof(AmiConnectionOptions.ReconnectInitialDelay) },
        { "ReconnectMaxDelay = Timeout.InfiniteTimeSpan", nameof(AmiConnectionOptions.ReconnectMaxDelay) },
        { "ReconnectMaxDelay = 60 days, above the int.MaxValue ms wait limit", nameof(AmiConnectionOptions.ReconnectMaxDelay) },
    };

    /// <summary>
    /// What a constructor rejects with <c>AutoReconnect</c> on: every value of <see cref="UnusableReconnectValues"/>, and a
    /// negative attempt limit, which the validator rejects through its range attribute but a constructor must check itself.
    /// </summary>
    public static TheoryData<string, string> ValuesAConstructorRejects
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var row in (IEnumerable<object[]>)UnusableReconnectValues)
                data.Add((string)row[0], (string)row[1]);
            data.Add("MaxReconnectAttempts = -1", nameof(AmiConnectionOptions.MaxReconnectAttempts));
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

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeFalse($"with AutoReconnect on, {value} is a value the reconnect backoff cannot use");
        result.FailureMessage.Should().Contain(option, "the failure names the option to fix");
    }

    [Theory]
    [MemberData(nameof(BoundaryReconnectValues))]
    public void Validate_ShouldSucceed_WhenTheBoundaryValuesAreUsed(string value)
    {
        var options = ReconnectOptions(value, autoReconnect: true);

        var result = _validator.Validate(null, options);

        result.Succeeded.Should().BeTrue($"{value} is a value the reconnect backoff accepts; failure: {result.FailureMessage}");
    }

    [Theory]
    [MemberData(nameof(UnusableReconnectValues))]
    public void Validate_ShouldSucceed_WhenAutoReconnectIsOffWithUnusableValues(string value, string option)
    {
        var options = ReconnectOptions(value, autoReconnect: false);

        var result = _validator.Validate(null, options);

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
                var validatorAccepts = _validator.Validate(null, options).Succeeded;

                validatorAccepts.Should().Be(BackoffAccepts(options) && WithinWaitLimit(options),
                    $"the validator accepts {value} if and only if BackoffSchedule.Compute does and no delay exceeds the wait limit");
            }
        }
    }

    /// <summary>
    /// Reconnect values the rule accepts, at its edges: a zero initial delay with a multiplier whose powers overflow a
    /// <see cref="double"/>, the largest finite multiplier, delays at the wait limit. Initial and maximum delay in ms.
    /// </summary>
    public static TheoryData<long, double, long> AcceptedReconnectEdges => new()
    {
        { 0, double.MaxValue, int.MaxValue },
        { 0, 2000.0, int.MaxValue },
        { 0, 1e10, 1000 },
        { 1, double.MaxValue, int.MaxValue },
        { int.MaxValue, 1.0, int.MaxValue },
        { 1000, 2.0, 30_000 },
    };

    /// <summary>
    /// "Accepted ⇒ <see cref="BackoffSchedule.Compute"/> accepts" must hold at every attempt the loop can reach, not only
    /// the first: the loop computes a delay on each iteration, and a value the rule accepted that throws there ends the
    /// reconnect. Every delay is also between zero and the maximum, so it is one the loop can wait.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedReconnectEdges))]
    public void Validate_ShouldAcceptOnlyValuesTheBackoffComputesAtEveryAttempt(long initialMs, double multiplier, long maxMs)
    {
        var options = ReconnectOptions("ReconnectMultiplier = 1", autoReconnect: true);
        options.ReconnectInitialDelay = TimeSpan.FromMilliseconds(initialMs);
        options.ReconnectMultiplier = multiplier;
        options.ReconnectMaxDelay = TimeSpan.FromMilliseconds(maxMs);
        int[] attempts = [1, 2, 3, 32, 50, 100, 101, 102, 1000, int.MaxValue];

        using (new AssertionScope())
        {
            _validator.Validate(null, options).Succeeded.Should().BeTrue("these values are inside the rule");
            foreach (var attempt in attempts)
            {
                var compute = () => BackoffSchedule.Compute(attempt, options.ReconnectInitialDelay, options.ReconnectMultiplier, options.ReconnectMaxDelay);
                compute.Should().NotThrow($"the rule accepted initial {initialMs} ms, multiplier {multiplier}, max {maxMs} ms, so attempt {attempt} is computable");
                if (Record.Exception(() => { _ = compute(); }) is null)
                    compute().Should().BeGreaterThanOrEqualTo(TimeSpan.Zero).And.BeLessThanOrEqualTo(options.ReconnectMaxDelay,
                        $"attempt {attempt}'s delay is one the loop can wait");
            }
        }
    }

    /// <summary>The longest delay .NET can wait: <see cref="int.MaxValue"/> ms, about 24.8 days (owner ruling Q2).</summary>
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromMilliseconds(int.MaxValue);

    private static bool WithinWaitLimit(AmiConnectionOptions options) =>
        options.ReconnectInitialDelay <= WaitLimit && options.ReconnectMaxDelay <= WaitLimit;

    private static bool BackoffAccepts(AmiConnectionOptions options)
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
    internal static AmiConnectionOptions ReconnectOptions(string value, bool autoReconnect)
    {
        var options = new AmiConnectionOptions
        {
            Hostname = "localhost",
            Port = 5038,
            Username = "admin",
            Password = "secret",
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
