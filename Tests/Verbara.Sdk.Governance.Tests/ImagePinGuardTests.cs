using Xunit.Abstractions;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// The functional lane's Asterisk and SIPp images are pinned by digest: no reference under <c>docker/</c>,
/// <c>.github/workflows/</c> or <c>Tests/</c> names one by a tag alone, every pinned Asterisk base is a line of
/// <c>docker/asterisk-base-images.txt</c>, and <c>docker/Dockerfile.asterisk</c> defaults to the table's 22 line and
/// refuses a base of another version. The fixtures build their image references from parts so the tree walk never
/// reads one in this file.
/// </summary>
public sealed class ImagePinGuardTests(ITestOutputHelper output)
{
    private static readonly string Asterisk = ImagePinScanner.AsteriskImage;
    private static readonly string Sipp = ImagePinScanner.SippImage;
    private static readonly string Digest22 = "@sha256:" + new string('2', 64);
    private static readonly string Digest23 = "@sha256:" + new string('3', 64);
    private static readonly string DigestSipp = "@sha256:" + new string('5', 64);
    private static readonly string Base22 = Asterisk + ":22.10.1_debian-trixie-0000000" + Digest22;
    private static readonly string Base23 = Asterisk + ":23.4.1_debian-trixie-0000000" + Digest23;

    private const string VersionCheck =
        "RUN asterisk -V | grep -Eq \"^Asterisk ${ASTERISK_VERSION}\\.\" || { echo \"base is $(asterisk -V), not Asterisk ${ASTERISK_VERSION}\" >&2; exit 1; }";

    [Fact]
    public void Tree_ShouldPinEveryAsteriskAndSippImageByDigest_WhenScanned()
    {
        var report = ImagePinScanner.Scan(RepoRoot());

        report.IsClean.Should().BeTrue(
            "every Asterisk and SIPp image reference under docker/, .github/workflows/ and Tests/ must carry a digest, " +
            "every pinned Asterisk base must be a line of the table, and the Dockerfile must default to its 22 line and " +
            "check the base's version. Found:" + Environment.NewLine + report);
        report.References.Should().Contain(r => r.Image == Asterisk && r.IsPinned)
            .And.Contain(r => r.Image == Sipp && r.IsPinned, "a guard that matched nothing would pass on nothing");
    }

    [Fact]
    public void Scan_ShouldPass_WhenEveryReferenceIsPinnedAndTheDockerfileMatchesTheTable()
    {
        using var tree = GreenTree();

        var report = ImagePinScanner.Scan(tree.Root);

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Fact]
    public void Scan_ShouldReportFoundNoReference_WhenTheTreeIsEmpty()
    {
        using var tree = new FixtureTree();

        var report = ImagePinScanner.Scan(tree.Root);
        output.WriteLine(report.ToString());

        report.Violations.Should().Contain(v => v.StartsWith("found no reference to " + Asterisk, StringComparison.Ordinal))
            .And.Contain(v => v.StartsWith("found no reference to " + Sipp, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("docker/docker-compose.extra.yml", "    image: {0}:23\n", 1)]
    [InlineData(".github/workflows/ci.yml", "jobs:\n  x:\n    steps:\n      - run: docker pull {1} &\n", 4)]
    [InlineData("Tests/Some/Container.cs", "var b = new ContainerBuilder(\"{1}:latest\");\n", 1)]
    [InlineData("docker/Dockerfile.other", "FROM {0}:${{ASTERISK_VERSION}}\n", 1)]
    public void Scan_ShouldNameFileAndLine_WhenAReferenceHasNoDigest(string path, string template, int line)
    {
        using var tree = GreenTree();
        tree.Write(path, string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Asterisk, Sipp));

        var report = ImagePinScanner.Scan(tree.Root);

        report.Violations.Should().ContainSingle(v => v.StartsWith($"{path}:{line}: ", StringComparison.Ordinal)
            && v.Contains("no @sha256 digest", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldReportAPinnedBaseOutsideTheTable_WhenACopyDrifts()
    {
        using var tree = GreenTree();
        var drifted = Asterisk + ":23.4.1_debian-trixie-1111111@sha256:" + new string('4', 64);
        tree.Write("docker/docker-compose.test-23.yml", $"services:\n  asterisk:\n    build:\n      args:\n        ASTERISK_BASE_IMAGE: {drifted}\n");

        var report = ImagePinScanner.Scan(tree.Root);

        report.Violations.Should().ContainSingle(v => v.StartsWith("docker/docker-compose.test-23.yml:5: ", StringComparison.Ordinal)
            && v.Contains("not a line of", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldReportEverySippReference_WhenTwoCarryDifferentDigests()
    {
        using var tree = GreenTree();
        var other = Sipp + "@sha256:" + new string('6', 64);
        tree.Write(".github/workflows/ci.yml", $"jobs:\n  x:\n    steps:\n      - run: docker pull {other} &\n");

        var report = ImagePinScanner.Scan(tree.Root);
        output.WriteLine(report.ToString());

        report.Violations.Should().ContainSingle(v => v.Contains("pinned by more than one digest", StringComparison.Ordinal)
                && v.Contains(".github/workflows/ci.yml:4", StringComparison.Ordinal)
                && v.Contains("Tests/Some/SippContainer.cs:1", StringComparison.Ordinal),
            "CI pre-pulling one SIPp image while the tests start another pulls an image nothing uses");
    }

    [Fact]
    public void Scan_ShouldPass_WhenEverySippReferenceCarriesTheSameDigest()
    {
        using var tree = GreenTree();
        tree.Write(".github/workflows/ci.yml", $"jobs:\n  x:\n    steps:\n      - run: docker pull {Sipp}{DigestSipp} &\n");

        var report = ImagePinScanner.Scan(tree.Root);

        report.IsClean.Should().BeTrue(report.ToString());
    }

    [Fact]
    public void Scan_ShouldReportTheDefault_WhenTheDockerfileDefaultIsNotThe22Line()
    {
        using var tree = GreenTree();
        tree.Write(ImagePinScanner.DockerfilePath, Dockerfile(Base23, withCheck: true));

        var report = ImagePinScanner.Scan(tree.Root);

        report.Violations.Should().Contain(v => v.Contains("the default base", StringComparison.Ordinal)
            && v.Contains("is not the 22 line", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldReportTheMissingCheck_WhenTheDockerfileDoesNotCheckTheBaseVersion()
    {
        using var tree = GreenTree();
        tree.Write(ImagePinScanner.DockerfilePath, Dockerfile(Base22, withCheck: false));

        var report = ImagePinScanner.Scan(tree.Root);

        report.Violations.Should().ContainSingle(v => v.Contains("no base-version check", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldReportTheTable_WhenItIsMissing()
    {
        using var tree = GreenTree();
        File.Delete(Path.Join(tree.Root, ImagePinScanner.TablePath));

        var report = ImagePinScanner.Scan(tree.Root);

        report.Violations.Should().Contain(v => v.StartsWith(ImagePinScanner.TablePath + ": missing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("22 {0}:23.4.1_debian-trixie-0000000{1}\n", "does not start with")]
    [InlineData("22 {0}:22.10.1_debian-trixie-0000000\n", "has no @sha256 digest")]
    [InlineData("22\n", "is not '<version> <reference>'")]
    public void Scan_ShouldReportTheLine_WhenATableLineIsWrong(string line, string expected)
    {
        using var tree = GreenTree();
        tree.Write(ImagePinScanner.TablePath,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, line, Asterisk, Digest22) + $"23 {Base23}\n");

        var report = ImagePinScanner.Scan(tree.Root);

        report.Violations.Should().Contain(v => v.StartsWith(ImagePinScanner.TablePath + ":1: ", StringComparison.Ordinal)
            && v.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ShouldSkipBuildOutput_WhenAFloatingReferenceIsUnderBinOrObj()
    {
        using var tree = GreenTree();
        tree.Write("Tests/Some/bin/Release/copy.yml", $"image: {Asterisk}:22\n");
        tree.Write("Tests/Some/obj/copy.cs", $"\"{Sipp}\"\n");

        var report = ImagePinScanner.Scan(tree.Root);

        report.IsClean.Should().BeTrue(report.ToString());
    }

    private static FixtureTree GreenTree()
    {
        var tree = new FixtureTree();
        tree.Write(ImagePinScanner.TablePath, $"# version reference\n22 {Base22}\n23 {Base23}\n");
        tree.Write(ImagePinScanner.DockerfilePath, Dockerfile(Base22, withCheck: true));
        tree.Write("docker/docker-compose.test-23.yml", $"      args:\n        ASTERISK_BASE_IMAGE: {Base23}\n");
        tree.Write("Tests/Some/SippContainer.cs", $"new ContainerBuilder(\"{Sipp}{DigestSipp}\");\n");
        return tree;
    }

    private static string Dockerfile(string defaultBase, bool withCheck) =>
        $"ARG ASTERISK_VERSION=22\nARG ASTERISK_BASE_IMAGE={defaultBase}\nFROM ${{ASTERISK_BASE_IMAGE}}\nARG ASTERISK_VERSION\nUSER root\n"
        + (withCheck ? VersionCheck + "\n" : "")
        + "USER asterisk\n";

    private static string RepoRoot() => Path.GetDirectoryName(TestTreeSource.TestsRoot())!;

    private sealed class FixtureTree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("image-pin-").FullName;

        public void Write(string relative, string text)
        {
            var path = Path.Join(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
