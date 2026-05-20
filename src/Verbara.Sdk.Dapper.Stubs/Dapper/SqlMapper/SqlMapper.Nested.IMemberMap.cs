using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>AOT-clean stub. Interface definition only.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
        Justification = "Drop-in mirror of Dapper.SqlMapper.IMemberMap — member name 'Property' MUST match real Dapper.")]
    public interface IMemberMap
    {
        string ColumnName { get; }
        Type MemberType { get; }
        PropertyInfo? Property { get; }
        FieldInfo? Field { get; }
        ParameterInfo? Parameter { get; }
    }
}
