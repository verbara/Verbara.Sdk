namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The root <c>.editorconfig</c> keeps the undeclared-public-API diagnostic (RS0016) a build failure: every
/// <c>dotnet_diagnostic.RS0016.severity</c> line is <c>warning</c> or <c>error</c>, at least one exists, and no key
/// addresses the public-API analyzer or sets a bulk severity. The guard script judges every analyzer-config file a
/// compile read, after the build; this test fails the unit lane on the root file before any build has run.
/// </summary>
public sealed class PublicApiSeverityGuardTests
{
    private const string Why =
        "a specific RS0016 severity in the root .editorconfig is what keeps dotnet_analyzer_diagnostic.severity = none " +
        "and the category-ApiDesign demotion inert for RS0016 (measured both ways), and a later line or a later " +
        "section overrides it silently — none, silent and suggestion each build green with an undeclared public " +
        "member; a dotnet_public_api_analyzer option such as skip_namespaces tells the analyzer to look away, " +
        "which nothing else in the build shows";

    [Fact]
    public void RootEditorConfig_ShouldKeepRs0016ABuildFailure_WhenScanned()
    {
        var path = Path.Join(Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName, ".editorconfig");

        var report = PublicApiSeverityScanner.Scan(File.ReadAllText(path), ".editorconfig");

        report.IsClean.Should().BeTrue($"{Why}. Found:{Environment.NewLine}{report}");
        report.SeverityLines.Should().BeGreaterThan(0, Why);
    }

    [Theory]
    [InlineData("root = true\n[*.cs]\ndotnet_diagnostic.RS0016.severity = warning  # comment\n")]
    [InlineData("[*.cs]\ndotnet_diagnostic.rs0016.severity = error\n")]
    [InlineData("[*.cs]\r\ndotnet_diagnostic.RS0016.severity = warning\r\n")]
    [InlineData("# RS0016: dotnet_diagnostic.RS0016.severity = none in a comment is not a setting\n[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\n")]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\ndotnet_code_quality.excluded_symbol_names = Probe\n")]
    public void Scan_ShouldPass_WhenEverySeverityLineFailsTheBuild(string text)
    {
        var report = PublicApiSeverityScanner.Scan(text, "fixture");

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Theory]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\ndotnet_diagnostic.RS0016.severity = none\n", "'none'")]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\ndotnet_diagnostic.RS0016.severity = silent\n", "'silent'")]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\n\n[src/**.cs]\ndotnet_diagnostic.RS0016.severity = suggestion\n", "'suggestion'")]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\ndotnet_diagnostic.RS0016.severity: none\r\n", "'none'")]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\ndotnet_public_api_analyzer.skip_namespaces = Foo\n", "dotnet_public_api_analyzer")]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\nDOTNET_PUBLIC_API_ANALYZER.SKIP_NAMESPACES = Foo\n", "dotnet_public_api_analyzer")]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\ndotnet_analyzer_diagnostic.severity = none\n", "dotnet_analyzer_diagnostic")]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = warning\ndotnet_analyzer_diagnostic.category-ApiDesign.severity = none\n", "dotnet_analyzer_diagnostic")]
    [InlineData("[*.cs]\ndotnet_diagnostic.CA1000.severity = none\n", "no dotnet_diagnostic.RS0016.severity line")]
    public void Scan_ShouldReportTheLine_WhenRs0016CanStopFailingTheBuild(string text, string expected)
    {
        var report = PublicApiSeverityScanner.Scan(text, "fixture");

        report.IsClean.Should().BeFalse("the fixture silences or demotes RS0016");
        report.ToString().Should().Contain(expected);
    }
}
