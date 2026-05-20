using System.Data;

namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.IWrappedDataReader</c>. Interface definition only —
/// real Dapper returns instances of internal types implementing this when ExecuteReader runs.
/// Mirrored here so consumer code that holds an <see cref="IWrappedDataReader"/> reference
/// compiles against the stub assembly.
/// </summary>
public interface IWrappedDataReader : IDataReader, IDisposable
{
    /// <summary>The underlying command that produced the reader.</summary>
    IDbCommand Command { get; }

    /// <summary>The underlying ADO.NET data reader being wrapped.</summary>
    IDataReader Reader { get; }
}
