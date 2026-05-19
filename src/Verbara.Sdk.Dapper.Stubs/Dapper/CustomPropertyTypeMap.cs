using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.CustomPropertyTypeMap</c>. Real Dapper uses this to let consumers
/// override the property-selection logic during row materialisation by passing a
/// <c>Func&lt;Type, string, PropertyInfo&gt;</c> chooser. Dapper.AOT replaces the materialiser
/// entirely, so this stub throws on every member.
/// </summary>
/// <remarks>
/// Annotations are intentionally omitted on the <see cref="SqlMapper.ITypeMap"/> overrides — the
/// interface carries no <c>[RequiresDynamicCode]</c>/<c>[RequiresUnreferencedCode]</c>, so adding
/// them would trip IL2046/IL3051. The constructor IS annotated since it is the top-level virtual
/// entrypoint and not a parity-constrained override.
/// </remarks>
[SuppressMessage("Performance", "CA1822:Mark members as static",
    Justification = "Drop-in mirror of Dapper.CustomPropertyTypeMap — instance shape MUST match real Dapper. Stub methods throw NotSupportedException; in real Dapper they read instance state.")]
public sealed class CustomPropertyTypeMap : SqlMapper.ITypeMap
{
    /// <summary>
    /// Initialise the map. Real Dapper retains <paramref name="type"/> + <paramref name="propertySelector"/>
    /// and invokes them during materialisation; the stub accepts both but never inspects them.
    /// </summary>
    [RequiresDynamicCode("Real Dapper materialises rows via DynamicMethod IL emission. Dapper.AOT replaces the materialiser.")]
    [RequiresUnreferencedCode("Real Dapper reflects over the target type's properties.")]
    public CustomPropertyTypeMap(Type type, Func<Type, string, PropertyInfo> propertySelector)
        => throw new NotSupportedException(
            "Dapper.CustomPropertyTypeMap stub — Dapper.AOT did not intercept the parent materialiser call site.");

    /// <inheritdoc/>
    public ConstructorInfo? FindConstructor(string[] names, Type[] types)
        => throw new NotSupportedException("Dapper.CustomPropertyTypeMap stub.");

    /// <inheritdoc/>
    public ConstructorInfo? FindExplicitConstructor()
        => throw new NotSupportedException("Dapper.CustomPropertyTypeMap stub.");

    /// <inheritdoc/>
    public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName)
        => throw new NotSupportedException("Dapper.CustomPropertyTypeMap stub.");

    /// <inheritdoc/>
    public SqlMapper.IMemberMap? GetMember(string columnName)
        => throw new NotSupportedException("Dapper.CustomPropertyTypeMap stub.");
}
