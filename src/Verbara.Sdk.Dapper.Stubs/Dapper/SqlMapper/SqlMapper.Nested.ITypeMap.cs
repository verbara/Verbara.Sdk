using System.Reflection;

namespace Dapper;

public static partial class SqlMapper
{
    /// <summary>AOT-clean stub. Interface definition only.</summary>
    public interface ITypeMap
    {
        ConstructorInfo? FindConstructor(string[] names, Type[] types);
        ConstructorInfo? FindExplicitConstructor();
        IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName);
        IMemberMap? GetMember(string columnName);
    }
}
