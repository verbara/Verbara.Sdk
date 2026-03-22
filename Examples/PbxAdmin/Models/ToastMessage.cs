namespace PbxAdmin.Models;

public sealed record ToastMessage(
    string Id, ToastLevel Level, string Title,
    string? Detail, DateTimeOffset Timestamp, bool AutoDismiss);
