using System.Data;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>AOT-clean stub. Interface definition only.</summary>
    public interface IDynamicParameters
    {
        void AddParameters(IDbCommand command, Identity identity);
    }
}
