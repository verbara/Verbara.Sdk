using System.Data;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>AOT-clean stub. Interface definition only.</summary>
    public interface ITypeHandler
    {
        void SetValue(IDbDataParameter parameter, object? value);
        object? Parse(Type destinationType, object value);
    }
}
