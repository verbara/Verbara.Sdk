namespace DashboardExample.Models;

public class ExtensionViewModel
{
    public string Extension { get; set; } = "";
    public string? Name { get; set; }
    public ExtensionTechnology Technology { get; set; }
    public ExtensionStatus Status { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public bool VoicemailEnabled { get; set; }
    public bool DndEnabled { get; set; }
    public string? CallForwardTo { get; set; }
}
