namespace Asterisk.Sdk.Sessions.Postgres;

/// <summary>
/// Options controlling the behaviour of <see cref="PostgresSessionStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SchemaName"/> and <see cref="TableName"/> are embedded into generated SQL
/// (Dapper/Npgsql do not parameterize identifiers). Only trusted values must be set; the
/// <c>UsePostgres</c> extensions validate both against
/// <c>^[A-Za-z_][A-Za-z0-9_]*$</c> at registration time.
/// </para>
/// </remarks>
public sealed class PostgresSessionStoreOptions
{
    /// <summary>
    /// Optional Npgsql connection string (e.g. <c>"Host=localhost;Database=asterisk;Username=postgres;Password=..."</c>).
    /// When set and no <see cref="global::Npgsql.NpgsqlDataSource"/> is already registered, the
    /// <c>UsePostgres</c> extension will create a singleton data source from this value. When an
    /// external data source is supplied (via <c>UsePostgres(NpgsqlDataSource, ...)</c>) this
    /// property is ignored.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Name of the table that stores session snapshots. Default: <c>asterisk_call_sessions</c>.
    /// Must match <c>^[A-Za-z_][A-Za-z0-9_]*$</c> (validated at registration time).
    /// </summary>
    public string TableName { get; set; } = "asterisk_call_sessions";

    /// <summary>
    /// Name of the schema that owns the sessions table. Default: <c>public</c>.
    /// Must match <c>^[A-Za-z_][A-Za-z0-9_]*$</c> (validated at registration time).
    /// </summary>
    public string SchemaName { get; set; } = "public";
}
