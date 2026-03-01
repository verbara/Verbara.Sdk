namespace Asterisk.Sdk.Config;

/// <summary>
/// Specialized parser for Asterisk extensions.conf (dialplan).
/// Extends ConfigFileReader with dialplan-specific parsing:
///   exten => 100,1,Answer()
///   same => n,Playback(hello-world)
///   same => n,Hangup()
/// </summary>
public sealed class ExtensionsConfigFileReader
{
    /// <summary>Parse an extensions.conf file.</summary>
    public static ExtensionsConfigFile Parse(string filePath)
    {
        using var reader = new StreamReader(filePath);
        return Parse(reader, filePath);
    }

    /// <summary>Parse from a TextReader.</summary>
    public static ExtensionsConfigFile Parse(TextReader reader, string? fileName = null)
    {
        var configFile = ConfigFileReader.Parse(reader, fileName);
        var result = new ExtensionsConfigFile { FileName = configFile.FileName };

        foreach (var category in configFile.Categories)
        {
            var context = new DialplanContext { Name = category.Name };

            string? currentExten = null;
            int currentPriority = 0;

            foreach (var variable in category.OrderedVariables)
            {
                if (string.Equals(variable.Key, "exten", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = variable.Value.Split(',', 3);
                    if (parts.Length >= 3)
                    {
                        currentExten = parts[0].Trim();
                        var priorityStr = parts[1].Trim();
                        currentPriority = priorityStr == "n" ? currentPriority + 1 :
                            int.TryParse(priorityStr, out var p) ? p : 1;
                        var application = parts[2].Trim();

                        context.Extensions.Add(new DialplanExtension
                        {
                            Extension = currentExten,
                            Priority = currentPriority,
                            Application = application
                        });
                    }
                }
                else if (string.Equals(variable.Key, "same", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = variable.Value.Split(',', 2);
                    if (parts.Length >= 2 && currentExten is not null)
                    {
                        var priorityStr = parts[0].Trim();
                        currentPriority = priorityStr == "n" ? currentPriority + 1 :
                            int.TryParse(priorityStr, out var p) ? p : currentPriority + 1;
                        var application = parts[1].Trim();

                        context.Extensions.Add(new DialplanExtension
                        {
                            Extension = currentExten,
                            Priority = currentPriority,
                            Application = application
                        });
                    }
                }
                else if (string.Equals(variable.Key, "include", StringComparison.OrdinalIgnoreCase))
                {
                    context.Includes.Add(variable.Value.Trim());
                }
            }

            result.Contexts.Add(context);
        }

        return result;
    }
}

/// <summary>Parsed extensions.conf with dialplan contexts.</summary>
public sealed class ExtensionsConfigFile
{
    public string FileName { get; set; } = string.Empty;
    public List<DialplanContext> Contexts { get; } = [];

    public DialplanContext? GetContext(string name) =>
        Contexts.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A dialplan context containing extensions.</summary>
public sealed class DialplanContext
{
    public string Name { get; init; } = string.Empty;
    public List<DialplanExtension> Extensions { get; } = [];
    public List<string> Includes { get; } = [];
}

/// <summary>A single dialplan extension entry.</summary>
public sealed class DialplanExtension
{
    public string Extension { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string Application { get; init; } = string.Empty;
}
