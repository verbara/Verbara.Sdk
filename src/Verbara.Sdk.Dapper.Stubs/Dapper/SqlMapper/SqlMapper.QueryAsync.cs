using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    // -----------------------------------------------------------------------
    // QueryAsync family — async equivalent of Query. Includes multi-mapping
    // (2..7 types + dynamic-types overload) and the QueryUnbufferedAsync
    // streaming variant (DbConnection-typed only, returns IAsyncEnumerable).
    //
    // CommandDefinition-typed overloads (Task 6 / Phase C.4) sit at the bottom:
    // untyped + typed-by-Type + generic single-type + 6 multi-mapping variants
    // (T1..T7). Real Dapper does NOT expose multi-mapping CommandDefinition
    // overloads via Func<object[], TReturn> — only the strongly-typed Func<…>.
    // -----------------------------------------------------------------------

    // ---- Untyped + typed-by-Type async ----
    // Source-typed as IEnumerable<dynamic> in real Dapper; IL is IEnumerable<object> + DynamicAttribute.
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<dynamic>> QueryAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<dynamic>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<object>> QueryAsync(this IDbConnection cnn, Type type, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<object>>(new NotSupportedException(StubMessage));

    // ---- Generic single-type async ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<T>> QueryAsync<T>(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<T>>(new NotSupportedException(StubMessage));

    // ---- Multi-mapping async (2..7) ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TFourth, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TFourth, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TFourth, TFifth, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn>(this IDbConnection cnn, string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    // ---- Dynamic-types Type[] + Func<object[], TReturn> ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TReturn>(this IDbConnection cnn, string sql, Type[] types, Func<object[], TReturn> map, object? param = null, IDbTransaction? transaction = null, bool buffered = true, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    // ---- Unbuffered streaming async (DbConnection-typed only) ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IAsyncEnumerable<dynamic> QueryUnbufferedAsync(this System.Data.Common.DbConnection cnn, string sql, object? param = null, System.Data.Common.DbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IAsyncEnumerable<T> QueryUnbufferedAsync<T>(this System.Data.Common.DbConnection cnn, string sql, object? param = null, System.Data.Common.DbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    // ---- CommandDefinition-typed overloads ----
    // Untyped — source-typed as IEnumerable<dynamic>; IL is IEnumerable<object>.
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<dynamic>> QueryAsync(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<IEnumerable<dynamic>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<object>> QueryAsync(this IDbConnection cnn, Type type, CommandDefinition command)
        => Task.FromException<IEnumerable<object>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<T>> QueryAsync<T>(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<IEnumerable<T>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TReturn>(this IDbConnection cnn, CommandDefinition command, Func<TFirst, TSecond, TReturn> map, string splitOn = "Id")
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TReturn>(this IDbConnection cnn, CommandDefinition command, Func<TFirst, TSecond, TThird, TReturn> map, string splitOn = "Id")
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TFourth, TReturn>(this IDbConnection cnn, CommandDefinition command, Func<TFirst, TSecond, TThird, TFourth, TReturn> map, string splitOn = "Id")
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TFourth, TFifth, TReturn>(this IDbConnection cnn, CommandDefinition command, Func<TFirst, TSecond, TThird, TFourth, TFifth, TReturn> map, string splitOn = "Id")
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn>(this IDbConnection cnn, CommandDefinition command, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn> map, string splitOn = "Id")
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IEnumerable<TReturn>> QueryAsync<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn>(this IDbConnection cnn, CommandDefinition command, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn> map, string splitOn = "Id")
        => Task.FromException<IEnumerable<TReturn>>(new NotSupportedException(StubMessage));
}
