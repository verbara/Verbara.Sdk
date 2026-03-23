namespace PbxAdmin.Services.CallFlow;

public static class NumberManipulator
{
    public static string Apply(string number, string? prefix, string? prepend)
    {
        var result = number;
        if (!string.IsNullOrEmpty(prefix) && result.StartsWith(prefix, StringComparison.Ordinal))
            result = result[prefix.Length..];
        if (!string.IsNullOrEmpty(prepend))
            result = prepend + result;
        return result;
    }

    public static string Preview(string? prefix, string? prepend)
    {
        var prefixLen = prefix?.Length ?? 0;
        var before = (prefix ?? "") + new string('X', 7);
        var after = (prepend ?? "") + new string('X', 7);
        if (prefixLen == 0 && string.IsNullOrEmpty(prepend))
            return "";
        return $"{before} → {after}";
    }
}
