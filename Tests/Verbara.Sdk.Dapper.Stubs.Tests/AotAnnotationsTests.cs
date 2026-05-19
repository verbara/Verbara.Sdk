using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

/// <summary>
/// Every public stub method whose body throws <see cref="NotSupportedException"/> MUST carry both
/// <see cref="RequiresDynamicCodeAttribute"/> and <see cref="RequiresUnreferencedCodeAttribute"/>
/// so ILC trims them cleanly during Native AOT publish. Working implementations (constructors +
/// pure property getters + interface dispatchers + no-op disposers) are exempt by type or by
/// method-name.
/// </summary>
/// <remarks>
/// <para><b>Override / explicit-interface-impl parity:</b> methods that implement an interface
/// member or override a base method whose declaration carries NO annotations MUST drop the
/// annotations on the implementation as well — otherwise IL2046/IL3051 fire. We therefore only
/// audit methods whose annotation set is locally authoritative, i.e. <c>m.GetBaseDefinition() == m</c>
/// AND the method is not an implicit interface implementation of an unannotated interface method.</para>
///
/// <para><b>Skipped categories:</b> abstract methods (no body to trim), interface declarations
/// (no body to trim), the constructor + accessor-only working-impl types listed in
/// <c>ExemptTypes</c>, and the explicit no-throw working-impl members listed in
/// <c>ExemptMethodNames</c>.</para>
/// </remarks>
public sealed class AotAnnotationsTests
{
    private static readonly Type[] ExemptTypes =
    [
        // Working impls — bodies are runtime-safe, no reflection involved.
        typeof(global::Dapper.CommandDefinition),         // value type: ctor + property getters
        typeof(global::Dapper.CommandFlags),              // enum
        typeof(global::Dapper.DynamicParameters),         // dictionary-backed Add/Get/ParameterNames; throw-bodies on its trim-hot members ARE annotated separately
        typeof(global::Dapper.ExplicitConstructorAttribute), // attribute marker with default ctor only
        typeof(global::Dapper.IWrappedDataReader),        // interface — no method bodies to trim
        typeof(global::Dapper.SqlMapper.Settings),        // static class with backing fields, no throws
    ];

    /// <summary>
    /// Specific declaring-type + method-name pairs that are working impls (no throw body) and
    /// therefore have nothing to trim. Kept narrow so future additions of throwing methods
    /// to the same type are still audited.
    /// </summary>
    private static readonly HashSet<string> ExemptMethodIdentifiers =
    [
        // GridReader Dispose/DisposeAsync are no-ops in the stub (real Dapper has working impls too).
        "Dapper.SqlMapper+GridReader::Dispose",
        "Dapper.SqlMapper+GridReader::DisposeAsync",
    ];

    [Fact]
    public void StubMethods_ShouldHave_RequiresDynamicCode_And_RequiresUnreferencedCode()
    {
        var stubs = typeof(global::Dapper.CommandDefinition).Assembly;
        var offenders = new List<string>();

        foreach (var type in stubs.GetExportedTypes())
        {
            if (ExemptTypes.Contains(type)) continue;
            if (type.FullName is null) continue;
            if (!type.FullName.StartsWith("Dapper.", StringComparison.Ordinal) && type.FullName != "Dapper") continue;

            // Interface declarations have no body to trim — IL2046 forbids annotating them
            // unilaterally if the consumer's interface signature would diverge from real Dapper.
            if (type.IsInterface) continue;

            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                if (m.IsAbstract) continue; // no body to trim
                if (ExemptMethodIdentifiers.Contains($"{type.FullName}::{m.Name}")) continue;
                if (!IsAnnotationAuthoritative(m)) continue;

                var hasRdc = m.GetCustomAttribute<RequiresDynamicCodeAttribute>() is not null;
                var hasRuc = m.GetCustomAttribute<RequiresUnreferencedCodeAttribute>() is not null;
                if (!hasRdc || !hasRuc)
                {
                    offenders.Add($"{type.FullName}::{m.Name} -- hasRDC={hasRdc} hasRUC={hasRuc}");
                }
            }
        }

        offenders.Should().BeEmpty(
            $"every stub method must carry both AOT annotations so ILC trims the throw bodies cleanly. " +
            $"Offenders ({offenders.Count}):\n{string.Join("\n", offenders.Take(20))}");
    }

    /// <summary>
    /// A method's annotations are locally authoritative when its declaration is not constrained
    /// by interface/base parity (which IL2046/IL3051 would enforce). Returns <c>false</c> for
    /// implicit interface implementations of unannotated interface methods and for overrides of
    /// unannotated virtual base methods — those MUST drop the annotations.
    /// </summary>
    private static bool IsAnnotationAuthoritative(MethodInfo m)
    {
        // Overrides of an annotated/unannotated base method are not authoritative (GetBaseDefinition
        // points to the original virtual declaration).
        if (m.GetBaseDefinition() != m) return false;

        // Implicit interface implementations: scan every interface the declaring type implements,
        // and for each interface look up the method in the interface map. If our method is the
        // target of any unannotated interface slot, we MUST drop annotations.
        if (m.DeclaringType is { IsClass: true } declaringType)
        {
            foreach (var iface in declaringType.GetInterfaces())
            {
                InterfaceMapping map;
                try
                {
                    map = declaringType.GetInterfaceMap(iface);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                for (int i = 0; i < map.TargetMethods.Length; i++)
                {
                    if (map.TargetMethods[i] != m) continue;
                    var ifaceMethod = map.InterfaceMethods[i];
                    var ifaceHasRdc = ifaceMethod.GetCustomAttribute<RequiresDynamicCodeAttribute>() is not null;
                    var ifaceHasRuc = ifaceMethod.GetCustomAttribute<RequiresUnreferencedCodeAttribute>() is not null;
                    if (!ifaceHasRdc || !ifaceHasRuc) return false;
                }
            }
        }

        return true;
    }
}
