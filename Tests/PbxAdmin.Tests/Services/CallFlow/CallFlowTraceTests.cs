using FluentAssertions;
using PbxAdmin.Models;
using PbxAdmin.Services.CallFlow;

namespace PbxAdmin.Tests.Services.CallFlow;

public class CallFlowTraceTests
{
    private const string ServerId = "server1";

    // -----------------------------------------------------------------------
    // Asterisk pattern matching
    // -----------------------------------------------------------------------

    [Fact]
    public void MatchesAsteriskPattern_ShouldMatch_ExactMatch()
    {
        CallFlowService.MatchesAsteriskPattern("5551234", "5551234").Should().BeTrue();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldNotMatch_ExactMismatch()
    {
        CallFlowService.MatchesAsteriskPattern("5551234", "5559999").Should().BeFalse();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldMatch_XDigit()
    {
        // X = [0-9]
        CallFlowService.MatchesAsteriskPattern("_XXXX", "1234").Should().BeTrue();
        CallFlowService.MatchesAsteriskPattern("_XXXX", "0000").Should().BeTrue();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldNotMatch_XDigit_WrongLength()
    {
        CallFlowService.MatchesAsteriskPattern("_XXXX", "123").Should().BeFalse();
        CallFlowService.MatchesAsteriskPattern("_XXXX", "12345").Should().BeFalse();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldMatch_NDigit()
    {
        // N = [2-9]
        CallFlowService.MatchesAsteriskPattern("_NXXX", "2345").Should().BeTrue();
        CallFlowService.MatchesAsteriskPattern("_NXXX", "9000").Should().BeTrue();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldNotMatch_NDigit_WhenFirstIs1()
    {
        CallFlowService.MatchesAsteriskPattern("_NXXX", "1345").Should().BeFalse();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldNotMatch_NDigit_WhenFirstIs0()
    {
        CallFlowService.MatchesAsteriskPattern("_NXXX", "0345").Should().BeFalse();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldMatch_ZDigit()
    {
        // Z = [1-9]
        CallFlowService.MatchesAsteriskPattern("_ZXXX", "1234").Should().BeTrue();
        CallFlowService.MatchesAsteriskPattern("_ZXXX", "9999").Should().BeTrue();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldNotMatch_ZDigit_WhenFirstIs0()
    {
        CallFlowService.MatchesAsteriskPattern("_ZXXX", "0234").Should().BeFalse();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldMatch_DotWildcard()
    {
        // . = one or more remaining characters
        CallFlowService.MatchesAsteriskPattern("_1X.", "12345").Should().BeTrue();
        CallFlowService.MatchesAsteriskPattern("_1X.", "123456789").Should().BeTrue();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldNotMatch_DotWildcard_TooShort()
    {
        // "." needs at least one more char after _1X
        CallFlowService.MatchesAsteriskPattern("_1X.", "12").Should().BeFalse();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldMatch_BangWildcard()
    {
        // ! = zero or more remaining characters
        CallFlowService.MatchesAsteriskPattern("_1X!", "12").Should().BeTrue();
        CallFlowService.MatchesAsteriskPattern("_1X!", "12345").Should().BeTrue();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldNotMatch_WhenPatternDoesNotMatch()
    {
        CallFlowService.MatchesAsteriskPattern("_NXXNXXXXXX", "123").Should().BeFalse();
    }

    [Fact]
    public void MatchesAsteriskPattern_ShouldMatch_NANPA()
    {
        // Full NANPA pattern: _1NXXNXXXXXX (11 digits starting with 1, N, X, X, N...)
        CallFlowService.MatchesAsteriskPattern("_1NXXNXXXXXX", "18005551234").Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Trace: inbound routing
    // -----------------------------------------------------------------------

    [Fact]
    public void Trace_ShouldMatchInboundRoute_ByExactDid()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Main Line",
                DidPattern = "5551234", DestinationType = "extension",
                Destination = "1001", Priority = 100, Enabled = true,
            },
        };

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], [], [], [], "5551234",
            DateTime.Now, "None");

        trace.RouteFound.Should().BeTrue();
        trace.Direction.Should().Be("Inbound");
        trace.Steps.Should().HaveCountGreaterOrEqualTo(1);
        trace.Steps[0].Description.Should().Contain("Inbound route matched");
        trace.Steps[0].Description.Should().Contain("Main Line");
    }

    [Fact]
    public void Trace_ShouldReturnNotFound_WhenNoMatch()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Main",
                DidPattern = "5551234", DestinationType = "extension",
                Destination = "1001", Priority = 100, Enabled = true,
            },
        };

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], [], [], [], "9999",
            DateTime.Now, "None");

        trace.RouteFound.Should().BeFalse();
        trace.Steps.Should().HaveCount(1);
        trace.Steps[0].Description.Should().Contain("No route found");
        trace.Steps[0].Description.Should().Contain("9999");
    }

    // -----------------------------------------------------------------------
    // Trace: time conditions
    // -----------------------------------------------------------------------

    [Fact]
    public void Trace_ShouldEvaluateTimeCondition_WhenOpen()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Hours Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "bh", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "bh",
                MatchDestType = "queue", MatchDest = "sales",
                NoMatchDestType = "voicemail", NoMatchDest = "1001",
                Enabled = true,
                Ranges =
                [
                    new() { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) },
                ],
            },
        };

        // Monday at 14:00
        var time = new DateTime(2026, 3, 23, 14, 0, 0); // Monday
        // Verify it's actually Monday
        time.DayOfWeek.Should().Be(DayOfWeek.Monday);

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], tcs, [], [], "5551234",
            time, "None");

        trace.RouteFound.Should().BeTrue();
        trace.Steps.Should().Contain(s => s.Result == "Matched" && s.EntityType == "TimeCondition");
        // Should follow open branch to queue
        trace.Steps.Should().Contain(s => s.Description.Contains("Queue") && s.Description.Contains("sales"));
    }

    [Fact]
    public void Trace_ShouldEvaluateTimeCondition_WhenClosed()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Hours Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "bh", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "bh",
                MatchDestType = "queue", MatchDest = "sales",
                NoMatchDestType = "voicemail", NoMatchDest = "1001",
                Enabled = true,
                Ranges =
                [
                    new() { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) },
                ],
            },
        };

        // Sunday at 14:00 (closed)
        var time = new DateTime(2026, 3, 22, 14, 0, 0); // Sunday
        time.DayOfWeek.Should().Be(DayOfWeek.Sunday);

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], tcs, [], [], "5551234",
            time, "None");

        trace.RouteFound.Should().BeTrue();
        trace.Steps.Should().Contain(s => s.Result == "NotMatched" && s.EntityType == "TimeCondition");
        // Should follow closed branch to voicemail
        trace.Steps.Should().Contain(s => s.Description.Contains("Voicemail") && s.Description.Contains("1001"));
    }

    [Fact]
    public void Trace_ShouldRespectOverrideMode_AllOpen()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "bh", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "bh",
                MatchDestType = "queue", MatchDest = "sales",
                NoMatchDestType = "voicemail", NoMatchDest = "1001",
                Enabled = true,
                Ranges =
                [
                    new() { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) },
                ],
            },
        };

        // Sunday (normally closed), but AllOpen forces open
        var time = new DateTime(2026, 3, 22, 14, 0, 0);

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], tcs, [], [], "5551234",
            time, "AllOpen");

        trace.RouteFound.Should().BeTrue();
        trace.Steps.Should().Contain(s => s.Result == "Matched" && s.EntityType == "TimeCondition");
        trace.Steps.Should().Contain(s => s.Description.Contains("Queue") && s.Description.Contains("sales"));
    }

    [Fact]
    public void Trace_ShouldRespectOverrideMode_AllClosed()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "bh", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "bh",
                MatchDestType = "queue", MatchDest = "sales",
                NoMatchDestType = "voicemail", NoMatchDest = "1001",
                Enabled = true,
                Ranges =
                [
                    new() { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) },
                ],
            },
        };

        // Monday at 14:00 (normally open), but AllClosed forces closed
        var time = new DateTime(2026, 3, 23, 14, 0, 0);

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], tcs, [], [], "5551234",
            time, "AllClosed");

        trace.RouteFound.Should().BeTrue();
        trace.Steps.Should().Contain(s => s.Result == "NotMatched" && s.EntityType == "TimeCondition");
        trace.Steps.Should().Contain(s => s.Description.Contains("Voicemail") && s.Description.Contains("1001"));
    }

    [Fact]
    public void Trace_ShouldRespectOverrideMode_Live()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Route",
                DidPattern = "5551234", DestinationType = "time_condition",
                Destination = "bh", Priority = 100, Enabled = true,
            },
        };

        var tcs = new List<TimeConditionConfig>
        {
            new()
            {
                Id = 10, ServerId = ServerId, Name = "bh",
                MatchDestType = "queue", MatchDest = "sales",
                NoMatchDestType = "voicemail", NoMatchDest = "1001",
                Enabled = true,
                Ranges =
                [
                    new() { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) },
                ],
            },
        };

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bh"] = "OPEN",
        };

        // Sunday (normally closed), but live override forces open
        var time = new DateTime(2026, 3, 22, 14, 0, 0);

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], tcs, overrides, [], "5551234",
            time, "Live");

        trace.RouteFound.Should().BeTrue();
        trace.Steps.Should().Contain(s => s.Result == "Matched" && s.EntityType == "TimeCondition");
        trace.Steps.Should().Contain(s => s.Description.Contains("Queue") && s.Description.Contains("sales"));
    }

    // -----------------------------------------------------------------------
    // Trace: IVR traversal
    // -----------------------------------------------------------------------

    [Fact]
    public void Trace_ShouldTraverseIvr()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "IVR Route",
                DidPattern = "5551234", DestinationType = "ivr",
                Destination = "main", Priority = 100, Enabled = true,
            },
        };

        var menus = new List<IvrMenuConfig>
        {
            new()
            {
                Id = 20, ServerId = ServerId, Name = "main", Label = "Main Menu",
                Greeting = "welcome", Timeout = 5, MaxRetries = 3,
                Items =
                [
                    new() { Digit = "1", DestType = "queue", DestTarget = "sales", Label = "Sales" },
                    new() { Digit = "2", DestType = "extension", DestTarget = "1001", Label = "Support" },
                    new() { Digit = "9", DestType = "hangup", DestTarget = "", Label = "Exit" },
                ],
            },
        };

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], [], [], menus, "5551234",
            DateTime.Now, "None");

        trace.RouteFound.Should().BeTrue();
        var ivrStep = trace.Steps.First(s => s.EntityType == "IvrMenu");
        ivrStep.Description.Should().Contain("IVR menu");
        ivrStep.Description.Should().Contain("main");
        ivrStep.Evaluation.Should().NotBeNullOrEmpty();
        ivrStep.Evaluation.Should().Contain("1");
        ivrStep.Evaluation.Should().Contain("2");
        ivrStep.Evaluation.Should().Contain("9");
    }

    // -----------------------------------------------------------------------
    // Trace: outbound routing
    // -----------------------------------------------------------------------

    [Fact]
    public void Trace_ShouldMatchOutboundPattern()
    {
        var outbound = new List<OutboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "US Long Distance",
                DialPattern = "_1NXXNXXXXXX", Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-a", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
        };

        var trace = CallFlowService.TraceCall(
            ServerId, [], outbound, [], [], [], "18005551234",
            DateTime.Now, "None");

        trace.RouteFound.Should().BeTrue();
        trace.Direction.Should().Be("Outbound");
        trace.Steps[0].Description.Should().Contain("Outbound route matched");
        trace.Steps[0].Description.Should().Contain("US Long Distance");
        trace.Steps.Should().Contain(s => s.Description.Contains("trunk") && s.Description.Contains("carrier-a"));
    }

    [Fact]
    public void Trace_ShouldShowNumberManipulation()
    {
        var outbound = new List<OutboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "External",
                DialPattern = "_9.", Prefix = "9", Prepend = "+1",
                Priority = 100, Enabled = true,
                Trunks = [new() { TrunkName = "carrier-a", TrunkTechnology = "PJSIP", Sequence = 1 }],
            },
        };

        var trace = CallFlowService.TraceCall(
            ServerId, [], outbound, [], [], [], "918005551234",
            DateTime.Now, "None");

        trace.RouteFound.Should().BeTrue();
        trace.Steps.Should().Contain(s => s.Description.Contains("Number manipulation"));
    }

    // -----------------------------------------------------------------------
    // Trace: DialplanLines
    // -----------------------------------------------------------------------

    [Fact]
    public void Trace_ShouldHaveDialplanLines()
    {
        var routes = new List<InboundRouteConfig>
        {
            new()
            {
                Id = 1, ServerId = ServerId, Name = "Main",
                DidPattern = "5551234", DestinationType = "extension",
                Destination = "1001", Priority = 100, Enabled = true,
            },
        };

        var trace = CallFlowService.TraceCall(
            ServerId, routes, [], [], [], [], "5551234",
            DateTime.Now, "None");

        trace.RouteFound.Should().BeTrue();
        trace.Steps.Where(s => s.Result != "NotFound")
            .Should().OnlyContain(s => s.DialplanLines.Count > 0);
    }
}
