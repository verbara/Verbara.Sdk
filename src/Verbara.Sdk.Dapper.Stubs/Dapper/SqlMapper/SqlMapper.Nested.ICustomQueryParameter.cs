using System.Data;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>AOT-clean stub. Interface definition only — implementations (e.g. user JsonbParameter) call AddParameter via virtual dispatch.</summary>
    public interface ICustomQueryParameter
    {
        void AddParameter(IDbCommand command, string name);
    }
}
