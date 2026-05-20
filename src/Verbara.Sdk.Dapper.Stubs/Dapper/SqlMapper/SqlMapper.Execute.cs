using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    // -----------------------------------------------------------------------
    // Execute family — sync + async non-query commands.
    //
    // Canonical stub template: each method carries [RequiresDynamicCode] +
    // [RequiresUnreferencedCode] referencing the shared messages declared in
    // SqlMapper.cs. Sync methods throw NotSupportedException directly; async
    // methods return Task.FromException<T> so awaiters can observe the failure
    // without thread-pool starvation on synchronous throw.
    //
    // CommandDefinition-typed overloads (Task 6 / Phase C.4) sit below their
    // (sql, param, …) counterparts to keep the grouping legible.
    // -----------------------------------------------------------------------

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static int Execute(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<int> ExecuteAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<int>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IDataReader ExecuteReader(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IDataReader> ExecuteReaderAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<IDataReader>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<System.Data.Common.DbDataReader> ExecuteReaderAsync(this System.Data.Common.DbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<System.Data.Common.DbDataReader>(new NotSupportedException(StubMessage));

    // ---- CommandDefinition-typed overloads ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static int Execute(this IDbConnection cnn, CommandDefinition command)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<int> ExecuteAsync(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<int>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IDataReader ExecuteReader(this IDbConnection cnn, CommandDefinition command)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IDataReader ExecuteReader(this IDbConnection cnn, CommandDefinition command, CommandBehavior commandBehavior)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IDataReader> ExecuteReaderAsync(this IDbConnection cnn, CommandDefinition command)
        => Task.FromException<IDataReader>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<IDataReader> ExecuteReaderAsync(this IDbConnection cnn, CommandDefinition command, CommandBehavior commandBehavior)
        => Task.FromException<IDataReader>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<System.Data.Common.DbDataReader> ExecuteReaderAsync(this System.Data.Common.DbConnection cnn, CommandDefinition command)
        => Task.FromException<System.Data.Common.DbDataReader>(new NotSupportedException(StubMessage));

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<System.Data.Common.DbDataReader> ExecuteReaderAsync(this System.Data.Common.DbConnection cnn, CommandDefinition command, CommandBehavior commandBehavior)
        => Task.FromException<System.Data.Common.DbDataReader>(new NotSupportedException(StubMessage));
}
