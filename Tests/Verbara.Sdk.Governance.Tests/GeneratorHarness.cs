using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// Runs the AMI source generators over a synthetic compilation and returns what they produced: each generated file by
/// generator and hint name, the generators' own diagnostics, and the diagnostics of the compilation once the generated
/// files are added to it.
/// </summary>
/// <remarks>
/// <para>
/// The real <c>Verbara.Sdk.Ami</c> build shows the generators' output only for the event types that assembly happens to
/// declare. A synthetic compilation lets a test choose them: an event with no obsolete member, one marked
/// <c>[Obsolete]</c>, one marked with a <c>DiagnosticId</c>.
/// </para>
/// <para>
/// <see cref="Stubs"/> declares, with the same names and namespaces, the few SDK types the generated code binds:
/// <c>ManagerEvent</c>, <c>VerbaraMappingAttribute</c>, <c>AmiMessage</c> and <c>AmiBoolean</c>. The compilation
/// references the framework assemblies of the running runtime only, so nothing of the SDK itself leaks into it.
/// </para>
/// </remarks>
internal static class GeneratorHarness
{
    /// <summary>The SDK types the generated registry and deserializer bind, declared as the SDK declares them.</summary>
    public const string Stubs = """
        #nullable enable
        using System;
        using System.Collections.Generic;

        namespace Verbara.Sdk.Attributes
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false)]
            public sealed class VerbaraMappingAttribute : Attribute
            {
                public VerbaraMappingAttribute(string name) => Name = name;

                public string Name { get; }
            }
        }

        namespace Verbara.Sdk
        {
            public class ManagerEvent
            {
                public string? Privilege { get; set; }
                public string? UniqueId { get; set; }
                public double? Timestamp { get; set; }
                public string? EventType { get; set; }
                public IReadOnlyDictionary<string, string>? RawFields { get; set; }
            }
        }

        namespace Verbara.Sdk.Ami
        {
            public static class AmiBoolean
            {
                public static bool? Parse(string? value) => value is null ? null : value == "true";
            }
        }

        namespace Verbara.Sdk.Ami.Internal
        {
            public sealed class AmiMessage
            {
                private readonly Dictionary<string, string> _fields;

                public AmiMessage(Dictionary<string, string> fields) => _fields = fields;

                public string? this[string key] => _fields.TryGetValue(key, out var value) ? value : null;

                public string? EventType => this["Event"];

                public IReadOnlyDictionary<string, string> Fields => _fields;
            }
        }
        """;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    /// <summary>
    /// Compiles <see cref="Stubs"/> plus <paramref name="source"/>, runs <paramref name="generators"/> over it and
    /// compiles the result.
    /// </summary>
    public static GeneratorRun Run(string source, params IIncrementalGenerator[] generators)
    {
        var compilation = CSharpCompilation.Create(
            "Verbara.Sdk.Governance.GeneratorProbe",
            [
                CSharpSyntaxTree.ParseText(Stubs, ParseOptions, path: "Stubs.cs"),
                CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Source.cs"),
            ],
            FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators.Select(static generator => generator.AsSourceGenerator()),
            parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var generated = driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources.Select(file => new GeneratedFile(
                result.Generator.GetGeneratorType().Name, file.HintName, file.SourceText.ToString())))
            .ToImmutableArray();

        return new GeneratorRun(generated, generatorDiagnostics, output.GetDiagnostics());
    }

    /// <summary>Every assembly of the running runtime's own directory: the framework, and nothing of the SDK.</summary>
    private static ImmutableArray<MetadataReference> FrameworkReferences()
    {
        var runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return
        [
            .. trusted
                .Where(p => string.Equals(Path.GetDirectoryName(p), runtimeDirectory, StringComparison.Ordinal))
                .Select(static p => MetadataReference.CreateFromFile(p)),
        ];
    }
}

/// <summary>One file a generator added: the generator's type name, the file's hint name and its text.</summary>
internal sealed record GeneratedFile(string Generator, string HintName, string Text);

/// <summary>What one <see cref="GeneratorHarness.Run"/> produced.</summary>
internal sealed record GeneratorRun(
    ImmutableArray<GeneratedFile> Files,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompilationDiagnostics)
{
    /// <summary>The one file <paramref name="generator"/> added; fails when it added none or several.</summary>
    public GeneratedFile Single(string generator) => Files.Single(f => f.Generator == generator);

    /// <summary>The compilation's warnings and errors, generated files included, one per line.</summary>
    public IReadOnlyList<string> WarningsAndErrors =>
    [
        .. CompilationDiagnostics
            .Where(static d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(static d => d.ToString()),
    ];
}
