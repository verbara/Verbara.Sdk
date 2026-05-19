using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.Dapper.Stubs.Tests;

/// <summary>
/// Reflection-based API surface comparison: every public member in real Dapper 2.1.72 must exist
/// in our stubs assembly with identical signature. Conversely, we must NOT add any public member
/// that isn't in real Dapper. This is the drop-in contract.
///
/// Uses <see cref="MetadataLoadContext"/> rather than <see cref="Assembly.LoadFrom(string)"/>
/// because both DLLs share the exact same <see cref="AssemblyName"/> ("Dapper, Version=2.1.72.0,
/// PublicKeyToken=null") by design — that's the drop-in identity. The default runtime loader
/// collapses identical identities to the first-loaded assembly, which would make both DLLs
/// report 0 exported types (silent green test). MetadataLoadContext gives us reflection-only
/// loading in an isolated context with no identity collision.
/// </summary>
public sealed class PublicApiSurfaceTests
{
    private const string RealDapperPath =
        "/home/orion75/.nuget/packages/dapper/2.1.72/lib/net8.0/Dapper.dll";

    private static string StubsDapperPath
    {
        get
        {
            var loc = typeof(PublicApiSurfaceTests).Assembly.Location;
            var dir = Path.GetDirectoryName(loc)!;
            return Path.Combine(dir, "Dapper.dll");
        }
    }

    /// <summary>
    /// Build a MetadataLoadContext with the .NET runtime assemblies + the target DLL on the
    /// resolver path so reflection over signatures resolves all referenced types.
    /// </summary>
    private static MetadataLoadContext CreateContext(string targetDll)
    {
        // Seed with the .NET runtime + target dll's directory so the resolver can find System.* etc.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeAssemblies = Directory.GetFiles(runtimeDir, "*.dll");
        var targetDir = Path.GetDirectoryName(targetDll)!;
        var targetDirAssemblies = Directory.GetFiles(targetDir, "*.dll");
        var allPaths = runtimeAssemblies.Concat(targetDirAssemblies).Concat(new[] { targetDll })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var resolver = new PathAssemblyResolver(allPaths);
        return new MetadataLoadContext(resolver);
    }

    private static Assembly LoadFromContext(MetadataLoadContext ctx, string path)
        => ctx.LoadFromAssemblyPath(path);

    /// <summary>Every public type in real Dapper must exist in stubs with the same full name.</summary>
    [Fact]
    public void Stubs_ShouldExpose_AllPublicTypes_FromRealDapper()
    {
        using var realCtx = CreateContext(RealDapperPath);
        using var stubCtx = CreateContext(StubsDapperPath);
        var realAsm = LoadFromContext(realCtx, RealDapperPath);
        var stubAsm = LoadFromContext(stubCtx, StubsDapperPath);

        var realTypes = realAsm.GetExportedTypes()
            .Select(t => t.FullName!)
            .Where(n => n.StartsWith("Dapper.", StringComparison.Ordinal) || n == "Dapper")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var stubTypes = stubAsm.GetExportedTypes()
            .Select(t => t.FullName!)
            .Where(n => n.StartsWith("Dapper.", StringComparison.Ordinal) || n == "Dapper")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var missing = realTypes.Except(stubTypes, StringComparer.Ordinal).ToList();
        var extra = stubTypes.Except(realTypes, StringComparer.Ordinal).ToList();

        missing.Should().BeEmpty(
            $"stubs must mirror every public type in real Dapper. Missing ({missing.Count}):\n{string.Join("\n", missing.Take(40))}");
        extra.Should().BeEmpty(
            $"stubs must NOT introduce types not in real Dapper. Extra ({extra.Count}):\n{string.Join("\n", extra.Take(40))}");
    }

    /// <summary>Every public method in real Dapper must exist in stubs with matching signature.</summary>
    [Fact]
    public void Stubs_ShouldExpose_AllPublicMethods_FromRealDapper()
    {
        using var realCtx = CreateContext(RealDapperPath);
        using var stubCtx = CreateContext(StubsDapperPath);
        var realAsm = LoadFromContext(realCtx, RealDapperPath);
        var stubAsm = LoadFromContext(stubCtx, StubsDapperPath);

        var realMethods = EnumeratePublicMethodSignatures(realAsm).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var stubMethods = EnumeratePublicMethodSignatures(stubAsm).OrderBy(s => s, StringComparer.Ordinal).ToList();

        var missing = realMethods.Except(stubMethods, StringComparer.Ordinal).ToList();
        var extra = stubMethods.Except(realMethods, StringComparer.Ordinal).ToList();

        missing.Should().BeEmpty(
            $"stubs must mirror every public method. Missing ({missing.Count}):\n{string.Join("\n", missing.Take(40))}");
        extra.Should().BeEmpty(
            $"stubs must NOT add methods not in real Dapper. Extra ({extra.Count}):\n{string.Join("\n", extra.Take(40))}");
    }

    private static IEnumerable<string> EnumeratePublicMethodSignatures(Assembly asm)
    {
        foreach (var type in asm.GetExportedTypes())
        {
            var typeName = type.FullName!;
            if (!typeName.StartsWith("Dapper.", StringComparison.Ordinal) && typeName != "Dapper") continue;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                // Exclude property accessors + event accessors — they're tested by property/event mirrors implicitly
                if (m.IsSpecialName) continue;
                yield return FormatSignature(type, m);
            }
        }
    }

    private static string FormatSignature(Type declaringType, MethodInfo m)
    {
        // Use Name fallback for both parameters AND return type: for methods with generic type
        // parameters, FullName is null (e.g. `T` returns null but Name returns "T"). Without this
        // symmetric fallback, signatures with generic returns render as "-> " (empty) which both
        // (a) is visually broken for the human reader, and (b) creates a latent collision risk
        // if real Dapper ever adds overloads differing only by generic return type.
        var paramStr = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
        var returnStr = m.ReturnType.FullName ?? m.ReturnType.Name;
        return $"{declaringType.FullName}::{m.Name}({paramStr}) -> {returnStr}";
    }
}
