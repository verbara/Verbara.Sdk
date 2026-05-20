using Npgsql;

namespace Verbara.Sdk.Data.Npgsql;

/// <summary>
/// AOT-clean execution facade over <see cref="NpgsqlDataSource"/>. Centralizes connection
/// open/dispose, command creation, parameter binding, and reader iteration that Dapper used
/// to provide. Callers supply an explicit, reflection-free bind delegate
/// and (for queries) a map delegate.
/// </summary>
public static class NpgsqlExecutor
{
    /// <summary>
    /// Executes a non-query SQL statement via <paramref name="dataSource"/> and returns the
    /// number of rows affected.
    /// </summary>
    public static async Task<int> ExecuteAsync(this NpgsqlDataSource dataSource, string sql,
        Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        bind(cmd.Parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a scalar SQL statement via <paramref name="dataSource"/> and returns the first
    /// column of the first row, cast to <typeparamref name="T"/>. Returns <see langword="default"/>
    /// when the result is <c>NULL</c>.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="T"/> must match the exact CLR type that the Npgsql provider boxes for
    /// the given Postgres type — no widening or narrowing is applied. For example,
    /// <c>COUNT(*)</c> and any <c>int8</c>/<c>bigint</c> column boxes a <see cref="long"/>, so
    /// callers must use <c>ExecuteScalarAsync&lt;long&gt;</c>; using <c>&lt;int&gt;</c> throws
    /// <see cref="InvalidCastException"/> at runtime.
    /// </remarks>
    public static async Task<T?> ExecuteScalarAsync<T>(this NpgsqlDataSource dataSource, string sql,
        Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        bind(cmd.Parameters);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>
    /// Executes a query via <paramref name="dataSource"/> and returns the first row mapped by
    /// <paramref name="map"/>, or <see langword="default"/> when no rows are returned.
    /// Throws <see cref="InvalidOperationException"/> when the query returns more than one row,
    /// matching the behaviour of <c>Dapper.QuerySingleOrDefault</c>.
    /// </summary>
    public static async Task<T?> QuerySingleOrDefaultAsync<T>(this NpgsqlDataSource dataSource, string sql,
        Action<NpgsqlParameterCollection> bind, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        bind(cmd.Parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return default;
        var result = map(reader);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Sequence contains more than one element.");
        return result;
    }

    /// <summary>
    /// Executes a query via <paramref name="dataSource"/> and returns all rows mapped by
    /// <paramref name="map"/> as a <see cref="List{T}"/>.
    /// </summary>
    public static async Task<List<T>> QueryListAsync<T>(this NpgsqlDataSource dataSource, string sql,
        Action<NpgsqlParameterCollection> bind, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        bind(cmd.Parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) results.Add(map(reader));
        return results;
    }

    /// <summary>
    /// Executes a non-query SQL statement on an existing <paramref name="connection"/>, optionally
    /// within a <paramref name="transaction"/>, and returns the number of rows affected.
    /// </summary>
    public static async Task<int> ExecuteAsync(this NpgsqlConnection connection, string sql,
        Action<NpgsqlParameterCollection> bind, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        bind(cmd.Parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
