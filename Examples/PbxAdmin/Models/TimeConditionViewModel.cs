namespace PbxAdmin.Models;

public sealed class TimeConditionViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string MatchDestType { get; set; } = "";
    public string MatchDest { get; set; } = "";
    public string? MatchDestLabel { get; set; }
    public string NoMatchDestType { get; set; } = "";
    public string NoMatchDest { get; set; } = "";
    public string? NoMatchDestLabel { get; set; }
    public bool Enabled { get; set; }
    public TimeConditionState CurrentState { get; set; }
    public int RangeCount { get; set; }
    public int HolidayCount { get; set; }
}
