namespace Asterisk.NetAot.Config;

/// <summary>
/// Parses Asterisk configuration files (.conf format).
/// Supports sections [name], key=value pairs, comments (;), and #include directives.
/// </summary>
public sealed class ConfigFileReader
{
    /// <summary>Parse an Asterisk .conf file.</summary>
    public ConfigFile Parse(string filePath)
    {
        // TODO: Implement .conf parser
        throw new NotImplementedException();
    }

    /// <summary>Parse from a TextReader.</summary>
    public ConfigFile Parse(TextReader reader)
    {
        // TODO: Implement .conf parser
        throw new NotImplementedException();
    }
}

/// <summary>Represents a parsed Asterisk configuration file.</summary>
public sealed class ConfigFile
{
    public string FileName { get; set; } = string.Empty;
    public List<ConfigCategory> Categories { get; set; } = [];
}

/// <summary>A configuration section [name].</summary>
public sealed class ConfigCategory
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Variables { get; set; } = [];
}
