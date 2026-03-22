namespace PbxAdmin.Models;

public sealed class NotificationEntry
{
    public required string Id { get; init; }
    public required ToastLevel Level { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public bool IsRead { get; set; }
}
