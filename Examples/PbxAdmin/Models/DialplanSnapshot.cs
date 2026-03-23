namespace PbxAdmin.Models;

public sealed class DialplanSnapshot
{
    public string ServerId { get; init; } = "";
    public DateTime RefreshedAt { get; init; }
    public IReadOnlyList<DiscoveredContext> Contexts { get; init; } = [];
}

public sealed class DiscoveredContext
{
    public string Name { get; init; } = "";
    public string CreatedBy { get; init; } = "";
    public bool IsSystem { get; init; }
    public IReadOnlyList<DialplanExtension> Extensions { get; init; } = [];
    public IReadOnlyList<string> Includes { get; init; } = [];
}

public sealed class DialplanExtension
{
    public string Pattern { get; init; } = "";
    public IReadOnlyList<DialplanPriority> Priorities { get; init; } = [];
}

public sealed class DialplanPriority
{
    public int Number { get; init; }
    public string? Label { get; init; }
    public string Application { get; init; } = "";
    public string ApplicationData { get; init; } = "";
    public string? Source { get; init; }
}
