namespace PbxAdmin.Models;

public sealed class HolidayEntry
{
    public string Name { get; set; } = "";
    public int Month { get; set; }
    public int Day { get; set; }
    public bool Recurring { get; set; } = true;
}
