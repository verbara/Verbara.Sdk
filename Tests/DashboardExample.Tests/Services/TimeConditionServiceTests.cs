using DashboardExample.Models;
using DashboardExample.Services;
using DashboardExample.Services.Dialplan;
using DashboardExample.Services.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DashboardExample.Tests.Services;

public class TimeConditionServiceTests
{
    [Fact]
    public void EvaluateState_ShouldReturnOpen_WhenInRange()
    {
        var now = new DateTime(2026, 3, 17, 10, 0, 0); // Tuesday 10:00
        var ranges = new List<TimeRangeEntry>
        {
            new() { DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }
        };
        TimeConditionService.EvaluateState(ranges, [], now).Should().Be(TimeConditionState.Open);
    }

    [Fact]
    public void EvaluateState_ShouldReturnClosed_WhenOutOfRange()
    {
        var now = new DateTime(2026, 3, 17, 20, 0, 0); // Tuesday 20:00
        var ranges = new List<TimeRangeEntry>
        {
            new() { DayOfWeek = DayOfWeek.Tuesday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }
        };
        TimeConditionService.EvaluateState(ranges, [], now).Should().Be(TimeConditionState.Closed);
    }

    [Fact]
    public void EvaluateState_ShouldReturnClosed_OnHoliday()
    {
        var now = new DateTime(2026, 12, 25, 10, 0, 0); // Xmas, in range
        var ranges = new List<TimeRangeEntry>
        {
            new() { DayOfWeek = DayOfWeek.Thursday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }
        };
        var holidays = new List<HolidayEntry>
        {
            new() { Name = "Xmas", Month = 12, Day = 25, Recurring = true }
        };
        TimeConditionService.EvaluateState(ranges, holidays, now).Should().Be(TimeConditionState.Closed);
    }

    [Fact]
    public void EvaluateState_ShouldReturnClosed_WhenWrongDay()
    {
        var now = new DateTime(2026, 3, 15, 10, 0, 0); // Sunday 10:00
        var ranges = new List<TimeRangeEntry>
        {
            new() { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0) }
        };
        TimeConditionService.EvaluateState(ranges, [], now).Should().Be(TimeConditionState.Closed);
    }

    // -----------------------------------------------------------------------
    // CRUD validation tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Create_ShouldReject_WhenNameEmpty()
    {
        var sut = CreateService();
        var config = new TimeConditionConfig
        {
            ServerId = "s1",
            Name = "",
            MatchDestType = "extension",
            MatchDest = "100",
            NoMatchDestType = "extension",
            NoMatchDest = "200",
            Ranges = [new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) }],
        };

        var (success, error) = await sut.CreateTimeConditionAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("Name");
    }

    [Fact]
    public async Task Create_ShouldReject_WhenNameInvalid()
    {
        var sut = CreateService();
        var config = new TimeConditionConfig
        {
            ServerId = "s1",
            Name = "bad name",
            MatchDestType = "extension",
            MatchDest = "100",
            NoMatchDestType = "extension",
            NoMatchDest = "200",
            Ranges = [new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0) }],
        };

        var (success, error) = await sut.CreateTimeConditionAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("letters, digits, and hyphens");
    }

    [Fact]
    public async Task Create_ShouldReject_WhenNoRanges()
    {
        var sut = CreateService();
        var config = new TimeConditionConfig
        {
            ServerId = "s1",
            Name = "office-hours",
            MatchDestType = "extension",
            MatchDest = "100",
            NoMatchDestType = "extension",
            NoMatchDest = "200",
            Ranges = [],
        };

        var (success, error) = await sut.CreateTimeConditionAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("time range");
    }

    [Fact]
    public async Task Create_ShouldReject_WhenRangeInvalid()
    {
        var sut = CreateService();
        var config = new TimeConditionConfig
        {
            ServerId = "s1",
            Name = "office-hours",
            MatchDestType = "extension",
            MatchDest = "100",
            NoMatchDestType = "extension",
            NoMatchDest = "200",
            Ranges = [new TimeRangeEntry { DayOfWeek = DayOfWeek.Monday, StartTime = new TimeOnly(17, 0), EndTime = new TimeOnly(9, 0) }],
        };

        var (success, error) = await sut.CreateTimeConditionAsync(config);

        success.Should().BeFalse();
        error.Should().Contain("before end time");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TimeConditionService CreateService()
    {
        var repoResolver = Substitute.For<IRouteRepositoryResolver>();
        var dialplanResolver = Substitute.For<IDialplanProviderResolver>();
        var ivrRepo = Substitute.For<IIvrMenuRepository>();
        var regenerator = new DialplanRegenerator(repoResolver, dialplanResolver, ivrRepo);
        var logger = Substitute.For<ILogger<TimeConditionService>>();

        // AsteriskMonitorService is not accessed during validation
        return new TimeConditionService(repoResolver, regenerator, null!, logger);
    }
}
