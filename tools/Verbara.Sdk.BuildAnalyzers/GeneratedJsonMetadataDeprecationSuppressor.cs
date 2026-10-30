using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Verbara.Sdk.BuildAnalyzers;

/// <summary>
/// Suppresses one of the SDK's own deprecation ids (<c>VSDK####</c>) in the files the System.Text.Json source generator
/// emits, and nowhere else. A <c>[JsonSerializable]</c> row for a deprecated type keeps its shipped <c>JsonTypeInfo</c>
/// getter (nothing is removed), the generated metadata names that type, and the generated header disables only
/// <c>CS0612</c>/<c>CS0618</c>, which a <c>DiagnosticId</c> replaces; a generated file takes no pragma of ours. A
/// hand-written reference, in <c>src/</c> or <c>Tests/</c>, still gets the diagnostic and sits under its own reasoned
/// <c>#pragma warning disable</c>. Never packed: referenced with <c>OutputItemType="Analyzer"</c>,
/// <c>ReferenceOutputAssembly="false"</c> and <c>PrivateAssets="all"</c>.
/// </summary>
/// <remarks>
/// One <see cref="SuppressionDescriptor"/> per family whose members sit in a <c>[JsonSerializable]</c> row: the next
/// deprecated JSON record adds its id here and nothing else. Scope is the tree path, not the diagnostic's text: the
/// generator writes under <c>…/System.Text.Json.SourceGeneration.JsonSourceGenerator/&lt;Context&gt;.&lt;Type&gt;.g.cs</c>
/// whether or not <c>EmitCompilerGeneratedFiles</c> is on.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GeneratedJsonMetadataDeprecationSuppressor : DiagnosticSuppressor
{
    private const string GeneratorSegment = "/System.Text.Json.SourceGeneration.JsonSourceGenerator/";

    private static readonly SuppressionDescriptor Vsdk0004 = new(
        id: "VSDKS0004",
        suppressedDiagnosticId: "VSDK0004",
        justification: "System.Text.Json's generator names a deprecated chan_websocket member whose shipped JsonTypeInfo getter stays; nothing hand-written is suppressed.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => ImmutableArray.Create(Vsdk0004);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var path = diagnostic.Location.SourceTree?.FilePath?.Replace('\\', '/');
            if (path is null
                || !path.EndsWith(".g.cs", StringComparison.Ordinal)
                || path.IndexOf(GeneratorSegment, StringComparison.Ordinal) < 0)
                continue;

            context.ReportSuppression(Suppression.Create(Vsdk0004, diagnostic));
        }
    }
}
