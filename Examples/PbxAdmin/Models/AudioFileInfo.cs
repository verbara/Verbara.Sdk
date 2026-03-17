namespace PbxAdmin.Models;

public sealed record AudioFileInfo(
    string Name,
    long Size,
    DateTime LastModified,
    TimeSpan? Duration);
