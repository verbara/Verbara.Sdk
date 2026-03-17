namespace PbxAdmin.Models;

public sealed class TimeConditionConfig
{
    public int Id { get; set; }
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string MatchDestType { get; set; } = "";
    public string MatchDest { get; set; } = "";
    public string NoMatchDestType { get; set; } = "";
    public string NoMatchDest { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<TimeRangeEntry> Ranges { get; set; } = [];
    public List<HolidayEntry> Holidays { get; set; } = [];
}
