namespace DashboardExample.Services;

public sealed class ConfigProviderOptions
{
    /// <summary>"Ami" or "Database".</summary>
    public string Type { get; set; } = "Ami";

    /// <summary>PostgreSQL connection string. Required when <see cref="Type"/> is "Database".</summary>
    public string? ConnectionString { get; set; }
}
