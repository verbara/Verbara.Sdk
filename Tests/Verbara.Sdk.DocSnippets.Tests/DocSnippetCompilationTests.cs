using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Verbara.Sdk.DocSnippets.Tests;

/// <summary>
/// Guard #2 of Fase 1 — compiles every ```csharp block extracted from the
/// repo's public Markdown documentation. Catches the "docs drift" failure mode
/// where a snippet references an SDK type or method that no longer exists,
/// renamed (e.g. Asterisk.* → Verbara.* during the v2.0 rebrand), or had its
/// signature changed.
///
/// Snippets are wrapped in a synthetic file-scope class + async method body
/// before compilation so they can use <c>await</c> and define local variables.
/// Snippets that contain a top-level <c>class</c> declaration (often nested
/// helper handlers in the README's Voice AI examples) are compiled in
/// "namespace-wrap" mode instead.
///
/// To deliberately exclude a snippet from compilation, add an HTML comment
/// <c>&lt;!-- skip-doc-snippet --&gt;</c> on the line immediately above the
/// opening triple-backtick fence.
/// </summary>
[SuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Reflection is OK in test code")]
[SuppressMessage("SingleFile", "IL3000:RequiresAssemblyFiles", Justification = "Tests never run from a single-file bundle")]
public sealed class DocSnippetCompilationTests
{
    [Theory]
    [MemberData(nameof(GetSnippets))]
    public void Snippet_ShouldCompile(string filePath, int lineNumber, string snippet)
    {
        var source = WrapSnippet(snippet, filePath, lineNumber);
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Regular));

        // ConsoleApplication output kind enables top-level statements + class
        // declarations in the same compilation unit, which matches the shape
        // of most README "Quick Start" snippets.
        var compilation = CSharpCompilation.Create(
            assemblyName: $"DocSnippet_{Path.GetFileNameWithoutExtension(filePath)}_L{lineNumber}",
            syntaxTrees: [tree],
            references: GetCompilationReferences(),
            options: new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                allowUnsafe: true));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} at {d.Location.GetLineSpan().StartLinePosition}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}")
            .ToList();

        errors.Should().BeEmpty(
            because: $"snippet at {filePath}:{lineNumber} must compile against the current SDK\n--- snippet ---\n{snippet}\n--- end ---");
    }

    public static TheoryData<string, int, string> GetSnippets()
    {
        var data = new TheoryData<string, int, string>();
        var repoRoot = GetRepoRoot();

        var sources = new[]
        {
            "README.md",
            "docs/README-technical.md",
            "docs/guides/asterisk-version-compatibility.md",
            "docs/guides/high-load-tuning.md",
            "docs/guides/session-store-backends.md",
            "docs/guides/troubleshooting.md",
        };

        foreach (var relativePath in sources)
        {
            var fullPath = Path.Combine(repoRoot, relativePath);
            if (!File.Exists(fullPath)) continue;

            var lines = File.ReadAllLines(fullPath);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() != "```csharp") continue;

                // Honor explicit opt-out marker on the line immediately above.
                if (i > 0 && lines[i - 1].Trim().Contains("<!-- skip-doc-snippet -->", StringComparison.Ordinal))
                {
                    continue;
                }

                var blockStart = i + 1;
                var blockEnd = blockStart;
                while (blockEnd < lines.Length && lines[blockEnd].TrimEnd() != "```")
                {
                    blockEnd++;
                }

                if (blockEnd >= lines.Length)
                {
                    // Unterminated fence — skip rather than emit a noise failure.
                    break;
                }

                var content = string.Join("\n", lines[blockStart..blockEnd]);
                data.Add(relativePath, blockStart + 1, content);
                i = blockEnd;
            }
        }

        return data;
    }

    private static string WrapSnippet(string snippet, string filePath, int lineNumber)
    {
        // Many snippets start with `using X.Y.Z;` directives that document which
        // namespaces the example uses. Lift those out so we can prepend our
        // own common-usings prelude and emit a clean top-level program shape.
        var (snippetUsings, snippetBody) = ExtractLeadingUsings(snippet);

        var commonUsings = $$"""
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using System.Reactive.Linq;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Hosting;
            using Verbara.Sdk;
            using Verbara.Sdk.Ami;
            using Verbara.Sdk.Ami.Actions;
            using Verbara.Sdk.Hosting;
            using Verbara.Sdk.Live.Server;
            using Verbara.Sdk.Agi.Mapping;
            using Verbara.Sdk.Agi.Server;
            using Verbara.Sdk.VoiceAi;
            using Verbara.Sdk.VoiceAi.OpenAiRealtime;
            {{snippetUsings}}
            """;

        // Top-level program shape lets a snippet mix statements, lambdas, and
        // class declarations (which is exactly the shape of README's Quick Start
        // and most VoiceAi examples). `string[] args` is implicitly available.
        // `await Task.Yield()` ensures the synthesized Main is async even if
        // the snippet itself has no awaits (avoids CS4014 false positives).
        return $$"""
            {{commonUsings}}

            await Task.Yield();
            #pragma warning disable CS0162, CS0168, CS0219, CS8321, CS1591
            {{snippetBody}}
            #pragma warning restore CS0162, CS0168, CS0219, CS8321, CS1591
            """;
    }

    /// <summary>
    /// Pulls leading <c>using X.Y;</c> directives off the front of the snippet so
    /// the wrapper can place them at file scope instead of inside the method body.
    /// </summary>
    private static (string Usings, string Body) ExtractLeadingUsings(string snippet)
    {
        var lines = snippet.Split('\n').ToList();
        var usings = new List<string>();
        var bodyStart = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("using ", StringComparison.Ordinal)
                && trimmed.EndsWith(';')
                && !trimmed.Contains('=', StringComparison.Ordinal))
            {
                usings.Add(trimmed);
                bodyStart = i + 1;
                continue;
            }

            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                // Tolerate blank lines between using directives or between usings and body.
                if (bodyStart > 0 && bodyStart == i)
                {
                    bodyStart = i + 1;
                }
                continue;
            }

            break;
        }

        return (string.Join('\n', usings), string.Join('\n', lines.Skip(bodyStart)));
    }

    /// <summary>
    /// Force-load assemblies that are not transitively pulled in by ProjectReference
    /// alone, or that .NET's lazy assembly loader holds back until first use.
    /// Roslyn needs each as a MetadataReference; if the assembly isn't loaded into
    /// the test process, <see cref="AppDomain.GetAssemblies"/> won't return it and
    /// Roslyn can't see its types.
    /// </summary>
    private static readonly Type[] _forceLoadAnchors =
    [
        typeof(System.Reactive.Linq.Observable),
        typeof(Microsoft.Extensions.Hosting.Host),
        typeof(Microsoft.Extensions.DependencyInjection.ServiceCollection),
        typeof(Microsoft.Extensions.Configuration.ConfigurationBuilder),
        typeof(Microsoft.Extensions.Hosting.IHostedService),
    ];

    /// <summary>
    /// Cache the metadata references once per process — building them is the
    /// expensive part. Roslyn's CSharpCompilation accepts the same MetadataReference
    /// list across many compilations.
    ///
    /// References are sourced from:
    ///   1. The .NET runtime's TRUSTED_PLATFORM_ASSEMBLIES list — every framework
    ///      assembly available to the host process (System.*, Microsoft.Extensions.*,
    ///      System.Reactive.*, etc.). These live in the runtime store, NOT in the
    ///      test project's bin/.
    ///   2. The test project's bin/ directory — every Verbara.Sdk.* assembly and
    ///      direct PackageReference (e.g. Npgsql for Sessions.Postgres, NATS.Client
    ///      for Push.Nats).
    /// </summary>
    private static readonly Lazy<IReadOnlyList<MetadataReference>> _references =
        new(() =>
        {
            // Touch _forceLoadAnchors so its initializer runs (defense in depth).
            _ = _forceLoadAnchors.Length;

            var dllPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // (1) Framework + nuget runtime assemblies via TRUSTED_PLATFORM_ASSEMBLIES.
            // CoreCLR exposes the canonical list of all probed managed DLLs that the
            // current process can load — this is what Roslyn-in-process tests use to
            // reproduce the host's reference set.
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrEmpty(tpa))
            {
                foreach (var p in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    dllPaths.Add(p);
                }
            }

            // (2) Anything copied to the test output that isn't already in TPA
            // (Verbara.Sdk.* ProjectReference outputs, transitive package DLLs).
            var binDir = AppContext.BaseDirectory;
            foreach (var p in Directory.EnumerateFiles(binDir, "*.dll"))
            {
                var name = Path.GetFileName(p);
                if (name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                dllPaths.Add(p);
            }

            var references = new List<MetadataReference>(dllPaths.Count);
            foreach (var p in dllPaths)
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(p));
                }
                catch (BadImageFormatException)
                {
                    // Native dll (e.g. SQLitePCL native binaries). Skip.
                }
                catch (IOException)
                {
                    // File missing or locked. Skip.
                }
            }

            return references.AsReadOnly();
        });

    private static IReadOnlyList<MetadataReference> GetCompilationReferences() => _references.Value;

    private static string GetRepoRoot()
    {
        // The test runs from bin/{Debug,Release}/net10.0/ — walk up until we
        // find the repo root marker (Verbara.Sdk.slnx).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Verbara.Sdk.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root (Verbara.Sdk.slnx).");
    }
}
