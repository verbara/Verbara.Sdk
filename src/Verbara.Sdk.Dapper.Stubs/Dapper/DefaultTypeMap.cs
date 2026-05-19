using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Dapper;

/// <summary>
/// AOT-clean stub of <c>Dapper.DefaultTypeMap</c>. Real Dapper uses this as the fallback type-map
/// when no custom map is registered; it walks properties + the chosen constructor via reflection.
/// Dapper.AOT replaces the entire materialiser, so the stub throws on every member except the
/// properties.
/// </summary>
/// <remarks>
/// Annotations are intentionally omitted on the <see cref="SqlMapper.ITypeMap"/> overrides — the
/// interface carries no <c>[RequiresDynamicCode]</c>/<c>[RequiresUnreferencedCode]</c>, so adding
/// them would trip IL2046/IL3051. The constructor IS annotated since it is the top-level virtual
/// entrypoint and not a parity-constrained override.
/// </remarks>
[SuppressMessage("Performance", "CA1822:Mark members as static",
    Justification = "Drop-in mirror of Dapper.DefaultTypeMap — instance shape MUST match real Dapper.")]
public sealed class DefaultTypeMap : SqlMapper.ITypeMap
{
    /// <summary>
    /// Initialise for <paramref name="type"/>. Real Dapper enumerates properties + constructors;
    /// the stub stores nothing.
    /// </summary>
    [RequiresDynamicCode("Real Dapper materialises rows via DynamicMethod IL emission. Dapper.AOT replaces the materialiser.")]
    [RequiresUnreferencedCode("Real Dapper reflects over the target type's properties and constructors.")]
    public DefaultTypeMap(Type type)
        => throw new NotSupportedException(
            "Dapper.DefaultTypeMap stub — Dapper.AOT did not intercept the parent materialiser call site.");

    /// <summary>
    /// Global default: when <c>true</c>, column <c>some_column</c> matches property <c>SomeColumn</c>.
    /// Static per real Dapper — switching it affects every <see cref="DefaultTypeMap"/> process-wide.
    /// </summary>
    public static bool MatchNamesWithUnderscores { get; set; }

    /// <summary>The properties of the underlying type (always throws in the stub — never reaches the getter body).</summary>
    public List<PropertyInfo> Properties
        => throw new NotSupportedException("Dapper.DefaultTypeMap stub.");

    /// <inheritdoc/>
    public ConstructorInfo? FindConstructor(string[] names, Type[] types)
        => throw new NotSupportedException("Dapper.DefaultTypeMap stub.");

    /// <inheritdoc/>
    public ConstructorInfo? FindExplicitConstructor()
        => throw new NotSupportedException("Dapper.DefaultTypeMap stub.");

    /// <inheritdoc/>
    public SqlMapper.IMemberMap? GetConstructorParameter(ConstructorInfo constructor, string columnName)
        => throw new NotSupportedException("Dapper.DefaultTypeMap stub.");

    /// <inheritdoc/>
    public SqlMapper.IMemberMap? GetMember(string columnName)
        => throw new NotSupportedException("Dapper.DefaultTypeMap stub.");
}
