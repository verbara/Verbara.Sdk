namespace DashboardExample.Models;

public sealed class TimeRangeEntry
{
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}
