namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "a test class's cleanup is one the runner calls" (ADR-0066): no test class
/// and no class or collection fixture under <c>Tests/</c> has <c>IAsyncDisposable</c> in its
/// interface closure. xunit 2.9.3 never
/// calls <c>IAsyncDisposable.DisposeAsync</c> on either, so a cleanup written there is dead code.
/// ZERO-TOLERANCE with no baseline: the conversion landed with the guard. Carries two liveness floors
/// — files walked and test classes recognised — and detector fixtures for every shape the rule
/// names. The rule is scoped to the pinned 2.x runner and is rewritten or retired at the xunit v3
/// migration, where <c>IAsyncLifetime</c> derives from <c>IAsyncDisposable</c> and the runner calls it.
/// </summary>
public sealed class TestClassCleanupGuardTests
{
    // Conservative floors, well below the tree as this guard landed (578 test files; 432 test
    // classes). The second floor is what keeps an empty violation list honest: a detector that
    // stopped matching [Fact] would recognise no test class and pass having judged nothing.
    private const int MinimumScannedFiles = 400;
    private const int MinimumTestClasses = 300;

    [Fact]
    public void Guard_ShouldFindNoRunnerManagedTypeWithIAsyncDisposable_WhenScanningTheTestTree()
    {
        var result = ScanTestTree();

        result.Violations.Should().BeEmpty(TestClassCleanupScanner.BuildFailureMessage(result.Violations));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTheTestTree()
    {
        var count = TestTreeSource.EnumerateTestSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the real Tests/ tree; a near-zero count means the locator broke and " +
            "the cleanup scan would be a false green");
    }

    [Fact]
    public void Guard_ShouldRecognizeManyTestClasses_WhenWalkingTheTestTree()
    {
        var result = ScanTestTree();

        result.TestClassCount.Should().BeGreaterThanOrEqualTo(
            MinimumTestClasses,
            "the detector must recognise the test classes Tests/ actually declares; finding almost none " +
            "means [Fact]/[Theory] detection broke, and an empty violation list would then mean nothing");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenATestClassImplementsOnlyIAsyncDisposable()
    {
        const string source =
            "namespace N;\n" +
            "public sealed class ServerTests : IAsyncDisposable {\n" +
            "    [Fact] public void A() { }\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}";

        var violations = Scan(source);

        violations.Should().ContainSingle().Which.Should().Be(
            new TestClassCleanupViolation("x.cs", 2, "N.ServerTests", "test class"));
    }

    [Theory]
    [InlineData("IAsyncLifetime")]
    [InlineData("Xunit.IAsyncLifetime")]
    [InlineData("global::Xunit.IAsyncLifetime")]
    [InlineData("IDisposable")]
    public void Scan_ShouldPass_WhenATestClassImplementsOnlyARunnerCalledCleanup(string cleanup)
    {
        var source =
            "namespace N;\n" +
            $"public sealed class ServerTests : {cleanup} {{\n" +
            "    [Fact] public void A() { }\n" +
            "}";

        var result = TestClassCleanupScanner.Scan([new TestClassCleanupSource("x.cs", source)]);

        result.Violations.Should().BeEmpty();
        result.TestClassCount.Should().Be(1, "the class was judged and passed");
    }

    [Theory]
    [InlineData("IAsyncDisposable, IDisposable")]
    [InlineData("IDisposable, IAsyncDisposable")]
    [InlineData("IAsyncLifetime, IAsyncDisposable")]
    [InlineData("Xunit.IAsyncLifetime, System.IAsyncDisposable")]
    public void Scan_ShouldFlag_WhenIAsyncDisposableSitsBesideARunnerCalledCleanup(string baseList)
    {
        // The runner calls only Dispose, or only the Task-returning IAsyncLifetime.DisposeAsync; an
        // explicit IAsyncDisposable.DisposeAsync holding real cleanup stays dead beside either.
        var source =
            "namespace N;\n" +
            $"public sealed class ServerTests : {baseList} {{\n" +
            "    [Fact] public void A() { }\n" +
            "}";

        Scan(source).Should().ContainSingle().Which.TypeName.Should().Be("N.ServerTests");
    }

    [Theory]
    [InlineData("using System;\nnamespace N;\n", "System.IAsyncDisposable")]
    [InlineData("namespace N;\n", "global::System.IAsyncDisposable")]
    [InlineData("using AD = System.IAsyncDisposable;\nnamespace N;\n", "AD")]
    public void Scan_ShouldFlag_WhenIAsyncDisposableIsSpelledQualifiedOrThroughAnAlias(string header, string spelling)
    {
        var source =
            header +
            $"public sealed class ServerTests : {spelling} {{\n" +
            "    [Fact] public void A() { }\n" +
            "}";

        Scan(source).Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAnAliasInsideABlockNamespaceNamesIAsyncDisposable()
    {
        const string source =
            "namespace N\n" +
            "{\n" +
            "    using Cleanup = global::System.IAsyncDisposable;\n" +
            "    public sealed class ServerTests : Cleanup {\n" +
            "        [Fact] public void A() { }\n" +
            "    }\n" +
            "}";

        Scan(source).Should().ContainSingle().Which.Line.Should().Be(4);
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheViolationIsInheritedFromABaseClass()
    {
        const string baseSource =
            "namespace N;\n" +
            "public abstract class ServerTestsBase : IAsyncDisposable {\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}";
        const string derivedSource =
            "namespace N;\n" +
            "public sealed class ServerTests : ServerTestsBase {\n" +
            "    [Fact] public void A() { }\n" +
            "}";

        var violations = Scan(("Base.cs", baseSource), ("Derived.cs", derivedSource));

        violations.Should().ContainSingle().Which.Should().Be(
            new TestClassCleanupViolation("Derived.cs", 2, "N.ServerTests", "test class"),
            "the abstract base is not a test class; the derived class inherits both the test and the dead cleanup");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheTestMethodsAreInheritedFromAnAbstractBase()
    {
        const string source =
            "namespace N;\n" +
            "public abstract class ContractTests {\n" +
            "    [Fact] public void A() { }\n" +
            "}\n" +
            "public sealed class ServerContractTests : ContractTests, IAsyncDisposable {\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}";

        Scan(source).Should().ContainSingle().Which.TypeName.Should().Be("N.ServerContractTests");
    }

    [Fact]
    public void Scan_ShouldPass_WhenANestedFakeInsideATestClassImplementsIAsyncDisposable()
    {
        const string source =
            "namespace N;\n" +
            "public sealed class ServerTests : IAsyncLifetime {\n" +
            "    [Fact] public void A() { }\n" +
            "    private sealed class FakeServer : IAsyncDisposable {\n" +
            "        public void Helper() { }\n" +
            "        public ValueTask DisposeAsync() => default;\n" +
            "    }\n" +
            "}";

        Scan(source).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldJudgeANestedTestClassOnItsOwnMembers_WhenItSitsInsideATestClass()
    {
        const string source =
            "namespace N;\n" +
            "public sealed class Outer : IAsyncDisposable {\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "    public sealed class InnerTests : IAsyncDisposable {\n" +
            "        [Fact] public void A() { }\n" +
            "        public ValueTask DisposeAsync() => default;\n" +
            "    }\n" +
            "}\n" +
            "public sealed class OtherTests : IAsyncLifetime {\n" +
            "    [Fact] public void B() { }\n" +
            "    public sealed class Helper : IAsyncDisposable {\n" +
            "        public ValueTask DisposeAsync() => default;\n" +
            "    }\n" +
            "}";

        var result = TestClassCleanupScanner.Scan([new TestClassCleanupSource("x.cs", source)]);

        result.Violations.Should().ContainSingle().Which.TypeName.Should().Be(
            "N.Outer.InnerTests",
            "Outer holds no [Fact] of its own, so its nested test class does not make it one");
        result.TestClassCount.Should().Be(2);
    }

    [Fact]
    public void Scan_ShouldPass_WhenANonTestClassImplementsIAsyncDisposable()
    {
        const string source =
            "namespace N;\n" +
            "public sealed class FakeWebSocketServer : IAsyncDisposable {\n" +
            "    public void Start() { }\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}";

        Scan(source).Should().BeEmpty();
    }

    [Theory]
    [InlineData("RequiresDockerFact")]
    [InlineData("RealtimeFact")]
    [InlineData("SkippableTheory")]
    [InlineData("Xunit.Fact")]
    [InlineData("FactAttribute")]
    public void Scan_ShouldTreatTheMethodAsATest_WhenItsAttributeEndsInFactOrTheory(string attribute)
    {
        var source =
            "namespace N;\n" +
            "public sealed class DockerTests : IAsyncDisposable {\n" +
            $"    [{attribute}] public void A() {{ }}\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}";

        Scan(source).Should().ContainSingle();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenAPartialClassSplitsItsBaseListAndItsFactAcrossTwoSources()
    {
        const string first =
            "namespace N;\n" +
            "public sealed partial class ConnectionTests : IAsyncDisposable {\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}";
        const string second =
            "namespace N;\n" +
            "public sealed partial class ConnectionTests {\n" +
            "    [Fact] public void A() { }\n" +
            "}";

        var violations = Scan(("First.cs", first), ("Second.cs", second));

        violations.Should().ContainSingle().Which.Should().Be(
            new TestClassCleanupViolation("First.cs", 2, "N.ConnectionTests", "test class"));
    }

    [Fact]
    public void Scan_ShouldFlagOnlyTheViolatingOne_WhenTwoSameNamedClassesLiveInDifferentNamespaces()
    {
        const string ari =
            "namespace Ari.Audio;\n" +
            "public class AudioSocketServerTests : IAsyncDisposable {\n" +
            "    [Fact] public void A() { }\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}";
        const string voiceAi =
            "namespace VoiceAi.AudioSocket;\n" +
            "public class AudioSocketServerTests : IAsyncLifetime {\n" +
            "    [Fact] public void A() { }\n" +
            "}";
        const string derived =
            "namespace VoiceAi.AudioSocket.Edge;\n" +
            "public sealed class EdgeTests : AudioSocketServerTests {\n" +
            "    [Fact] public void B() { }\n" +
            "}";

        var violations = Scan(("Ari.cs", ari), ("VoiceAi.cs", voiceAi), ("Edge.cs", derived));

        violations.Should().ContainSingle().Which.TypeName.Should().Be(
            "Ari.Audio.AudioSocketServerTests",
            "EdgeTests resolves its base through its enclosing namespace to the clean VoiceAi class");
    }

    [Fact]
    public void Scan_ShouldResolveTheBaseThroughAUsing_WhenTheSameNameExistsInAnotherNamespace()
    {
        const string ari =
            "namespace Ari.Audio;\n" +
            "public class ServerTestsBase : IAsyncDisposable {\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}";
        const string voiceAi =
            "namespace VoiceAi.Audio;\n" +
            "public class ServerTestsBase : IAsyncLifetime { }";
        const string derived =
            "using Ari.Audio;\n" +
            "namespace Consumer;\n" +
            "public sealed class DerivedTests : ServerTestsBase {\n" +
            "    [Fact] public void A() { }\n" +
            "}";

        var violations = Scan(("Ari.cs", ari), ("VoiceAi.cs", voiceAi), ("Derived.cs", derived));

        violations.Should().ContainSingle().Which.TypeName.Should().Be("Consumer.DerivedTests");
    }

    [Theory]
    [InlineData("IClassFixture")]
    [InlineData("ICollectionFixture")]
    [InlineData("Xunit.IClassFixture")]
    public void Scan_ShouldFlag_WhenAFixtureImplementsOnlyIAsyncDisposable(string fixtureInterface)
    {
        var source =
            "namespace N;\n" +
            "public sealed class PostgresFixture : IAsyncDisposable {\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}\n" +
            $"public sealed class StoreTests : {fixtureInterface}<PostgresFixture> {{\n" +
            "    [Fact] public void A() { }\n" +
            "}";

        Scan(source).Should().ContainSingle().Which.Should().Be(
            new TestClassCleanupViolation("x.cs", 2, "N.PostgresFixture", "fixture"));
    }

    [Fact]
    public void Scan_ShouldFlag_WhenACollectionDefinitionNamesAFixtureWithIAsyncDisposable()
    {
        const string source =
            "namespace N;\n" +
            "public sealed class RedisFixture : IAsyncDisposable {\n" +
            "    public ValueTask DisposeAsync() => default;\n" +
            "}\n" +
            "[CollectionDefinition(\"redis\")]\n" +
            "public sealed class RedisCollection : ICollectionFixture<RedisFixture> { }";

        Scan(source).Should().ContainSingle().Which.Role.Should().Be("fixture");
    }

    [Fact]
    public void Scan_ShouldPass_WhenAFixtureImplementsIAsyncLifetime()
    {
        const string source =
            "namespace N;\n" +
            "public sealed class PostgresFixture : IAsyncLifetime {\n" +
            "    public Task InitializeAsync() => Task.CompletedTask;\n" +
            "    public Task DisposeAsync() => Task.CompletedTask;\n" +
            "}\n" +
            "public sealed class StoreTests : IClassFixture<PostgresFixture> {\n" +
            "    [Fact] public void A() { }\n" +
            "}";

        var result = TestClassCleanupScanner.Scan([new TestClassCleanupSource("x.cs", source)]);

        result.Violations.Should().BeEmpty();
        result.FixtureCount.Should().Be(1, "the fixture was recognised and judged");
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenIAsyncDisposableIsOnlyMentionedInCommentsOrStrings()
    {
        const string source =
            "namespace N;\n" +
            "/// <summary>Used to be <c>: IAsyncDisposable</c>.</summary>\n" +
            "public sealed class ServerTests : IAsyncLifetime {\n" +
            "    // public sealed class Old : IAsyncDisposable { }\n" +
            "    [Fact] public void A() { var s = \"class X : IAsyncDisposable\"; }\n" +
            "}";

        Scan(source).Should().BeEmpty();
    }

    [Fact]
    public void BuildFailureMessage_ShouldNameTheFixAndEachSite_WhenThereAreViolations()
    {
        var message = TestClassCleanupScanner.BuildFailureMessage(
            [new TestClassCleanupViolation("Tests/X/ServerTests.cs", 12, "N.ServerTests", "test class")]);

        message.Should().Contain("IAsyncLifetime")
            .And.Contain("GC.SuppressFinalize")
            .And.Contain("CA1816")
            .And.Contain("CA1001")
            .And.Contain("Disposed via IAsyncLifetime")
            .And.Contain("ADR-0066")
            .And.Contain("Tests/X/ServerTests.cs:12 N.ServerTests (test class)");
    }

    private static TestClassCleanupResult ScanTestTree()
    {
        var repoRoot = Directory.GetParent(TestTreeSource.TestsRoot())!.FullName;
        var sources = TestTreeSource.EnumerateTestSources()
            .Select(file => new TestClassCleanupSource(
                Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText(file)));
        return TestClassCleanupScanner.Scan(sources);
    }

    private static IReadOnlyList<TestClassCleanupViolation> Scan(string source) =>
        TestClassCleanupScanner.Scan([new TestClassCleanupSource("x.cs", source)]).Violations;

    private static IReadOnlyList<TestClassCleanupViolation> Scan(params (string Path, string Source)[] sources) =>
        TestClassCleanupScanner.Scan(sources.Select(s => new TestClassCleanupSource(s.Path, s.Source))).Violations;
}
