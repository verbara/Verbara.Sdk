namespace DashboardExample.Services.Dialplan;

internal sealed record DialplanLine(string Context, string Exten, int Priority, string App, string AppData);
