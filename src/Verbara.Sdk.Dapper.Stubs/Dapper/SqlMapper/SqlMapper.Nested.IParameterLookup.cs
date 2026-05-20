namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>AOT-clean stub. Interface definition only. Extends IDynamicParameters in real Dapper.</summary>
    public interface IParameterLookup : IDynamicParameters
    {
        object? this[string name] { get; }
    }
}
