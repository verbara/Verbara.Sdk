using Npgsql;

namespace Verbara.Sdk.Data.Npgsql;

/// <summary>
/// Reflection-free, name-based typed getters over <see cref="NpgsqlDataReader"/>.
/// AOT-safe replacement for Dapper's reader-to-object materialization.
/// </summary>
public static class NpgsqlReaderExtensions
{
    /// <summary>Gets a <see cref="string"/> value by column name.</summary>
    public static string GetString(this NpgsqlDataReader r, string column) => r.GetString(r.GetOrdinal(column));

    /// <summary>Gets a nullable <see cref="string"/> value by column name; returns <see langword="null"/> for <c>DBNull</c>.</summary>
    public static string? GetStringOrNull(this NpgsqlDataReader r, string column)
    { var o = r.GetOrdinal(column); return r.IsDBNull(o) ? null : r.GetString(o); }

    /// <summary>Gets an <see cref="int"/> value by column name.</summary>
    public static int GetInt32(this NpgsqlDataReader r, string column) => r.GetInt32(r.GetOrdinal(column));

    /// <summary>Gets a nullable <see cref="int"/> value by column name; returns <see langword="null"/> for <c>DBNull</c>.</summary>
    public static int? GetInt32OrNull(this NpgsqlDataReader r, string column)
    { var o = r.GetOrdinal(column); return r.IsDBNull(o) ? null : r.GetInt32(o); }

    /// <summary>Gets a <see cref="long"/> value by column name.</summary>
    public static long GetInt64(this NpgsqlDataReader r, string column) => r.GetInt64(r.GetOrdinal(column));

    /// <summary>Gets a nullable <see cref="long"/> value by column name; returns <see langword="null"/> for <c>DBNull</c>.</summary>
    public static long? GetInt64OrNull(this NpgsqlDataReader r, string column)
    { var o = r.GetOrdinal(column); return r.IsDBNull(o) ? null : r.GetInt64(o); }

    /// <summary>Gets a <see cref="short"/> value by column name.</summary>
    public static short GetInt16(this NpgsqlDataReader r, string column) => r.GetInt16(r.GetOrdinal(column));

    /// <summary>Gets a <see cref="bool"/> value by column name.</summary>
    public static bool GetBoolean(this NpgsqlDataReader r, string column) => r.GetBoolean(r.GetOrdinal(column));

    /// <summary>Gets a nullable <see cref="bool"/> value by column name; returns <see langword="null"/> for <c>DBNull</c>.</summary>
    public static bool? GetBooleanOrNull(this NpgsqlDataReader r, string column)
    { var o = r.GetOrdinal(column); return r.IsDBNull(o) ? null : r.GetBoolean(o); }

    /// <summary>Gets a <see cref="DateTime"/> value by column name.</summary>
    public static DateTime GetDateTime(this NpgsqlDataReader r, string column) => r.GetDateTime(r.GetOrdinal(column));

    /// <summary>Gets a nullable <see cref="DateTime"/> value by column name; returns <see langword="null"/> for <c>DBNull</c>.</summary>
    public static DateTime? GetDateTimeOrNull(this NpgsqlDataReader r, string column)
    { var o = r.GetOrdinal(column); return r.IsDBNull(o) ? null : r.GetDateTime(o); }

    /// <summary>Gets a <see cref="DateTimeOffset"/> value by column name (maps <c>timestamptz</c>).</summary>
    public static DateTimeOffset GetDateTimeOffset(this NpgsqlDataReader r, string column) => r.GetFieldValue<DateTimeOffset>(r.GetOrdinal(column));

    /// <summary>Gets a nullable <see cref="DateTimeOffset"/> value by column name; returns <see langword="null"/> for <c>DBNull</c>.</summary>
    public static DateTimeOffset? GetDateTimeOffsetOrNull(this NpgsqlDataReader r, string column)
    { var o = r.GetOrdinal(column); return r.IsDBNull(o) ? null : r.GetFieldValue<DateTimeOffset>(o); }

    /// <summary>Gets a <see cref="Guid"/> value by column name.</summary>
    public static Guid GetGuid(this NpgsqlDataReader r, string column) => r.GetGuid(r.GetOrdinal(column));

    /// <summary>Gets a <see cref="DateOnly"/> value by column name (maps Postgres <c>date</c>).</summary>
    public static DateOnly GetDateOnly(this NpgsqlDataReader r, string column) => r.GetFieldValue<DateOnly>(r.GetOrdinal(column));

    /// <summary>Gets a <see cref="decimal"/> value by column name.</summary>
    public static decimal GetDecimal(this NpgsqlDataReader r, string column) => r.GetDecimal(r.GetOrdinal(column));

    /// <summary>Gets a <see cref="double"/> value by column name.</summary>
    public static double GetDouble(this NpgsqlDataReader r, string column) => r.GetDouble(r.GetOrdinal(column));
}
