namespace PbxAdmin.Services.Dialplan;

public sealed record DialplanLine(string Context, string Exten, int Priority, string App, string AppData);
