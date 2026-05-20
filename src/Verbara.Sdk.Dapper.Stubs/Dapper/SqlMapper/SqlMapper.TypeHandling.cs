using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace Dapper;

public static partial class SqlMapper
{
    // -----------------------------------------------------------------------
    // Catch-all partial covering every static SqlMapper method outside the
    // Execute/Query families. Groups by responsibility:
    //   - Type-handler registration (AddTypeHandler, AddTypeHandlerImpl, AddTypeMap,
    //     RemoveTypeMap, SetTypeMap, GetTypeMap, HasTypeHandler, ResetTypeHandlers).
    //   - Table-valued parameters (AsTableValuedParameter, SetTypeName, GetTypeName).
    //   - Result-set parsing helpers (Parse, GetRowParser, GetTypeDeserializer, AsList).
    //   - Diagnostic + cache (GetCachedSQL, GetCachedSQLCount, GetHashCollissions,
    //     PurgeQueryCache, Format).
    //   - Low-level parameter plumbing (CreateParamInfoGenerator, FindOrAddParameter,
    //     PackListParameters, ReplaceLiterals, LookupDbType, SetDbType,
    //     SanitizeParameterValue, ReadChar, ReadNullableChar).
    //   - Throw helpers (ThrowDataException, ThrowNullCustomQueryParameter).
    //
    // None of these depend on Dapper.CommandDefinition or Dapper.DynamicParameters,
    // so Phase C.3 ships the full surface — nothing deferred to Task 6 in this file.
    // -----------------------------------------------------------------------

    // ---- Type-handler registration ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void AddTypeHandler(Type type, ITypeHandler handler)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void AddTypeHandler<T>(TypeHandler<T> handler)
        => throw new NotSupportedException(StubMessage);

    [SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
        Justification = "Mirror of real Dapper.SqlMapper.AddTypeHandlerImpl — name is load-bearing for surface parity.")]
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void AddTypeHandlerImpl(Type type, ITypeHandler? handler, bool clone)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void AddTypeMap(Type type, DbType dbType)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void AddTypeMap(Type type, DbType dbType, bool useGetFieldValue)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void RemoveTypeMap(Type type)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void SetTypeMap(Type type, ITypeMap map)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static ITypeMap GetTypeMap(Type type)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static bool HasTypeHandler(Type type)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void ResetTypeHandlers()
        => throw new NotSupportedException(StubMessage);

    // ---- Table-valued parameters ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static ICustomQueryParameter AsTableValuedParameter(this DataTable table, string? typeName = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static ICustomQueryParameter AsTableValuedParameter<T>(this IEnumerable<T> list, string? typeName = null)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void SetTypeName(this DataTable table, string typeName)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static string GetTypeName(this DataTable table)
        => throw new NotSupportedException(StubMessage);

    // ---- Result-set parsing helpers ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<dynamic> Parse(this IDataReader reader)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<object> Parse(this IDataReader reader, Type type)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<T> Parse<T>(this IDataReader reader)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Func<IDataReader, object> GetRowParser(this IDataReader reader, Type type, int startIndex = 0, int length = -1, bool returnNullIfFirstMissing = false)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Func<System.Data.Common.DbDataReader, object> GetRowParser(this System.Data.Common.DbDataReader reader, Type type, int startIndex = 0, int length = -1, bool returnNullIfFirstMissing = false)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Func<IDataReader, T> GetRowParser<T>(this IDataReader reader, Type? concreteType = null, int startIndex = 0, int length = -1, bool returnNullIfFirstMissing = false)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Func<System.Data.Common.DbDataReader, T> GetRowParser<T>(this System.Data.Common.DbDataReader reader, Type? concreteType = null, int startIndex = 0, int length = -1, bool returnNullIfFirstMissing = false)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Func<IDataReader, object> GetTypeDeserializer(Type type, IDataReader reader, int startBound = 0, int length = -1, bool returnNullIfFirstMissing = false)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Func<System.Data.Common.DbDataReader, object> GetTypeDeserializer(Type type, System.Data.Common.DbDataReader reader, int startBound = 0, int length = -1, bool returnNullIfFirstMissing = false)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static List<T> AsList<T>(this IEnumerable<T>? source)
        => throw new NotSupportedException(StubMessage);

    // ---- Diagnostic + cache ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<Tuple<string, string, int>> GetCachedSQL(int ignoreHitCountAbove = int.MaxValue)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static int GetCachedSQLCount()
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IEnumerable<Tuple<int, int>> GetHashCollissions()
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void PurgeQueryCache()
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static string Format(object value)
        => throw new NotSupportedException(StubMessage);

    // ---- Low-level parameter plumbing ----
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static Action<IDbCommand, object> CreateParamInfoGenerator(Identity identity, bool checkForDuplicates, bool removeUnused)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static IDbDataParameter FindOrAddParameter(IDataParameterCollection parameters, IDbCommand command, string name)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void PackListParameters(IDbCommand command, string namePrefix, object value)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void ReplaceLiterals(this IParameterLookup parameters, IDbCommand command)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static DbType? LookupDbType(Type type, string name, bool demand, out ITypeHandler? handler)
    {
        handler = null;
        throw new NotSupportedException(StubMessage);
    }

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void SetDbType(IDataParameter parameter, object value)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static object SanitizeParameterValue(object value)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static char ReadChar(object value)
        => throw new NotSupportedException(StubMessage);

    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static char? ReadNullableChar(object value)
        => throw new NotSupportedException(StubMessage);

    // ---- Throw helpers ----
    [DoesNotReturn]
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void ThrowDataException(Exception ex, int index, IDataReader reader, object value)
        => throw new NotSupportedException(StubMessage);

    [DoesNotReturn]
    [RequiresDynamicCode(RequiresDynamicCodeMessage), RequiresUnreferencedCode(RequiresUnreferencedCodeMessage)]
    public static void ThrowNullCustomQueryParameter(string name)
        => throw new NotSupportedException(StubMessage);
}
