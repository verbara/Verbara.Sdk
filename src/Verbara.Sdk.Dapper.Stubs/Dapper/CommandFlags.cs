using System.Diagnostics.CodeAnalysis;

namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.CommandFlags</c>. Working enum — semantic values must match real Dapper
/// because <c>CommandDefinition.Buffered</c> (Phase C.4) reads them via bitwise mask.
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Drop-in mirror of Dapper 2.1.72 public API — name MUST match real Dapper.")]
public enum CommandFlags
{
    None = 0,
    Buffered = 1,
    Pipelined = 2,
    NoCache = 4,
}
