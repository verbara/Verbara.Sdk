using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Verbara.Sdk.BuildAnalyzers;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The suppressor silences <c>VSDK0004</c> only in a file the System.Text.Json source generator wrote: the same
/// diagnostic in a hand-written file, in another generator's file, or in a file at the generator's path without the
/// <c>.g.cs</c> suffix is reported, and a plain <c>CS0618</c> is never touched. Each case compiles two synthetic trees —
/// the declarations and one use — with the suppressor attached and suppressed diagnostics reported.
/// </summary>
public sealed class GeneratedJsonMetadataDeprecationSuppressorTests
{
    private const string JsonGeneratorPath =
        "/repo/src/Pkg/obj/Release/net10.0/System.Text.Json.SourceGeneration/System.Text.Json.SourceGeneration.JsonSourceGenerator/Ctx.Gone.g.cs";

    private const string Declarations = """
        namespace Probe;
        [System.Obsolete("Never published.", DiagnosticId = "VSDK0004")]
        public sealed record Gone(string? Cause);
        [System.Obsolete("Plain obsolete, no id.")]
        public sealed record Plain(string? Cause);
        """;

    private const string Use = """
        namespace Probe;
        internal static class Use
        {
            internal static readonly System.Type T = typeof(Gone);
            internal static readonly System.Type P = typeof(Plain);
        }
        """;

    [Theory]
    [InlineData(JsonGeneratorPath, true)]
    [InlineData("/repo/src/Pkg/Audio/Use.cs", false)]
    [InlineData("/repo/src/Pkg/obj/Release/net10.0/Other.Generator/Other.Generator.Gen/Ctx.Gone.g.cs", false)]
    [InlineData("/repo/src/Pkg/obj/Release/net10.0/System.Text.Json.SourceGeneration/System.Text.Json.SourceGeneration.JsonSourceGenerator/Ctx.Gone.cs", false)]
    public async Task ReportSuppressions_ShouldSuppressVsdk0004OnlyInTheJsonGeneratorsOutput_WhenTheTreePathSaysWhoWroteIt(string usePath, bool suppressed)
    {
        // Act
        var diagnostics = await Run(usePath);

        // Assert
        diagnostics.Should().ContainSingle(d => d.Id == "VSDK0004").Which.IsSuppressed.Should().Be(suppressed);
        diagnostics.Should().ContainSingle(d => d.Id == "CS0618").Which.IsSuppressed.Should().BeFalse("the suppressor declares VSDK0004 only");
    }

    private static async Task<ImmutableArray<Diagnostic>> Run(string usePath)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(
            "Probe",
            [
                CSharpSyntaxTree.ParseText(Declarations, path: "/repo/src/Pkg/Gone.cs"),
                CSharpSyntaxTree.ParseText(Use, path: usePath),
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var withSuppressor = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new GeneratedJsonMetadataDeprecationSuppressor()),
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true));
        return await withSuppressor.GetAllDiagnosticsAsync();
    }
}
