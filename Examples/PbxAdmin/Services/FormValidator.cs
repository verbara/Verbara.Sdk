namespace PbxAdmin.Services;

public static class FormValidator
{
    public static bool IsRequired(string? value) => !string.IsNullOrWhiteSpace(value);

    public static bool IsRequiredInt(int value) => value > 0;

    public static bool IsInRange(int value, int min, int max) => value >= min && value <= max;

    public static bool MinLength(string? value, int min) => value is not null && value.Length >= min;

    public static bool IsValidHost(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (Uri.CheckHostName(value) != UriHostNameType.Unknown || value == "0.0.0.0");

    public static bool IsValidPort(int value) => value >= 1 && value <= 65535;

    public static bool IsValidExtension(string? value, int rangeStart, int rangeEnd) =>
        int.TryParse(value, out var ext) && ext >= rangeStart && ext <= rangeEnd;

    public static bool HasCodecs(string? value) => !string.IsNullOrWhiteSpace(value);
}
