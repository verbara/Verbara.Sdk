using System.Data;

namespace Dapper;

/// <summary>
/// AOT-clean drop-in for <c>Dapper.CommandDefinition</c>. WORKING IMPLEMENTATION — Dapper.AOT-generated
/// interceptor code reads the property getters at runtime to construct the real query against
/// the underlying ADO.NET command. Constructor + property getters are trim-safe; no reflection.
/// </summary>
public readonly struct CommandDefinition
{
    /// <summary>
    /// Initialise a new <see cref="CommandDefinition"/>. Mirrors real Dapper 2.1.72 exactly:
    /// default <paramref name="flags"/> is <see cref="CommandFlags.Buffered"/> (the int value <c>1</c>).
    /// </summary>
    public CommandDefinition(
        string commandText,
        object? parameters = null,
        IDbTransaction? transaction = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CommandFlags flags = CommandFlags.Buffered,
        CancellationToken cancellationToken = default)
    {
        CommandText = commandText;
        Parameters = parameters;
        Transaction = transaction;
        CommandTimeout = commandTimeout;
        CommandType = commandType;
        Flags = flags;
        CancellationToken = cancellationToken;
    }

    /// <summary>The raw SQL text or stored-procedure name to execute.</summary>
    public string CommandText { get; }

    /// <summary>Parameter object (anonymous type, typed object, or <c>Dapper.DynamicParameters</c>).</summary>
    public object? Parameters { get; }

    /// <summary>Optional transaction to enlist the command in.</summary>
    public IDbTransaction? Transaction { get; }

    /// <summary>Override the connection-default timeout, in seconds.</summary>
    public int? CommandTimeout { get; }

    /// <summary>Override the command type (Text / StoredProcedure / TableDirect).</summary>
    public CommandType? CommandType { get; }

    /// <summary>Composite flag bag (Buffered / Pipelined / NoCache).</summary>
    public CommandFlags Flags { get; }

    /// <summary>Cooperative-cancel token threaded through async overloads.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary><c>true</c> when <see cref="CommandFlags.Buffered"/> is set.</summary>
    public bool Buffered => (Flags & CommandFlags.Buffered) != CommandFlags.None;

    /// <summary><c>true</c> when <see cref="CommandFlags.Pipelined"/> is set.</summary>
    public bool Pipelined => (Flags & CommandFlags.Pipelined) != CommandFlags.None;
}
