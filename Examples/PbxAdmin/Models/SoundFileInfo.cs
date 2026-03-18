namespace PbxAdmin.Models;

public sealed record SoundFileInfo(
    string Name,           // Without extension (Asterisk convention)
    string RelativePath,   // Relative to sounds base dir
    string Format,         // File extension without dot
    long Size,
    string Category);      // Subdirectory name (e.g., "en", "digits")
