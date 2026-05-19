using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    // -----------------------------------------------------------------------
    // QueryMultiple — runs a multi-statement SQL batch and returns a GridReader
    // (declared in SqlMapper.Nested.GridReader.cs) that walks each result set
    // sequentially via its own Read* methods.
    //
    // Deferred to Task 6 (Phase C.4) — CommandDefinition overloads:
    //   - QueryMultiple(this IDbConnection cnn, CommandDefinition)        -> GridReader
    //   - QueryMultipleAsync(this IDbConnection cnn, CommandDefinition)   -> Task<GridReader>
    // -----------------------------------------------------------------------

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static GridReader QueryMultiple(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Task<GridReader> QueryMultipleAsync(this IDbConnection cnn, string sql, object? param = null, IDbTransaction? transaction = null, int? commandTimeout = null, CommandType? commandType = null)
        => Task.FromException<GridReader>(new NotSupportedException(StubMessage));
}
