using DashboardExample.Models;
using DashboardExample.Services;
using FluentAssertions;

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
}
