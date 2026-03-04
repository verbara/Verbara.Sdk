namespace DashboardExample.Services;

/// <summary>
/// Maps Asterisk config filenames to their corresponding Realtime database tables.
/// </summary>
internal static class RealtimeTableMap
{
    public static IReadOnlyList<TableDescriptor> GetTables(string filename) => filename.ToLowerInvariant() switch
    {
        "pjsip.conf" =>
        [
            new("ps_endpoints", "id", "type", "endpoint"),
            new("ps_auths", "id", "type", "auth"),
            new("ps_aors", "id", "type", "aor"),
            new("ps_registrations", "id", "type", "registration"),
        ],
        "sip.conf" => [new("sippeers", "name", "type", null)],
        "iax.conf" => [new("iaxpeers", "name", "type", null)],
        "queues.conf" => [new("queue_table", "name", null, null), new("queue_members", "queue_name", null, null)],
        "voicemail.conf" => [new("voicemail", "mailbox", "context", null)],
        _ => [],
    };

    /// <summary>
    /// Finds the table whose <see cref="TableDescriptor.TypeValue"/> matches the given type,
    /// or falls back to the first table if no type column is defined.
    /// </summary>
    public static TableDescriptor? ResolveTable(IReadOnlyList<TableDescriptor> tables, Dictionary<string, string> variables)
    {
        if (tables.Count == 0) return null;

        // If tables have a TypeValue, match by the "type" variable
        var typeValue = variables.GetValueOrDefault("type");
        if (typeValue is not null)
        {
            foreach (var t in tables)
            {
                if (string.Equals(t.TypeValue, typeValue, StringComparison.OrdinalIgnoreCase))
                    return t;
            }
        }

        // Fallback: first table (for files like sip.conf, iax.conf with a single table)
        return tables[0];
    }
}

/// <summary>
/// Describes how an Asterisk Realtime table is structured.
/// </summary>
/// <param name="TableName">PostgreSQL table name.</param>
/// <param name="IdColumn">Primary key / identifier column.</param>
/// <param name="TypeColumn">Column that holds the section type (e.g., "endpoint", "auth"), or null.</param>
/// <param name="TypeValue">Expected value for <paramref name="TypeColumn"/> in this table, or null.</param>
internal sealed record TableDescriptor(string TableName, string IdColumn, string? TypeColumn, string? TypeValue);
