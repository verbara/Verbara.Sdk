using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "a speech client re-checks its token after reading a channel to its end": in
/// every non-abstract recognizer or synthesizer in <c>src/</c>, an <c>await foreach</c> over
/// <c>ReadAllAsync(token)</c> or a <c>while (await reader.WaitToReadAsync(token))</c> loop is followed by
/// <c>token.ThrowIfCancellationRequested();</c>. Zero tolerance: the eight WebSocket clients do so today.
/// </summary>
/// <remarks>
/// <para>
/// The reader's own token check comes before its read of the channel's state. A cancel landing between
/// the two, while the client's receive loop ends the channel cleanly in reaction to it, reached the caller
/// as a normal end of stream — or as an empty result when nothing had arrived — instead of
/// <see cref="OperationCanceledException"/>. Each client's own tests hold that window open and prove its
/// check; this guard is what reaches a client written after them.
/// </para>
/// <para>
/// Carries a liveness floor on the reads recognised, so a detector that stopped seeing the shape fails
/// instead of passing on an empty scan, and fixtures for both shapes: the spelling that ships, and each
/// way of getting the check wrong.
/// </para>
/// </remarks>
public sealed class ChannelReadCancelGuardTests
{
    // The eight WebSocket clients read their result channel this way as the guard landed.
    private const int MinimumRecognisedReads = 8;

    private static readonly string[] ChannelBackedClients =
    [
        "CartesiaSpeechSynthesizer",
        "DeepgramSpeechSynthesizer",
        "ElevenLabsSpeechSynthesizer",
        "LmntSpeechSynthesizer",
        "AssemblyAiSpeechRecognizer",
        "CartesiaSpeechRecognizer",
        "DeepgramSpeechRecognizer",
        "SpeechmaticsSpeechRecognizer",
    ];

    [Fact]
    public void Guard_ShouldFindTheTokenReCheckedAfterEveryChannelRead_WhenScanningTheSrcTree()
    {
        var violations = SrcSources()
            .SelectMany(file => ChannelReadCancelScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldRecogniseTheChannelReadOfEachWebSocketClient_WhenScanningTheSrcTree()
    {
        var sites = SrcSources()
            .SelectMany(file => ChannelReadCancelScanner.FindSites(File.ReadAllText(file.FullPath)))
            .ToList();

        sites.Count.Should().BeGreaterThanOrEqualTo(
            MinimumRecognisedReads,
            "each channel-backed WebSocket client reads its result channel in one of the two shapes; " +
            "fewer means the detector or the locator broke and the guard would pass having examined nothing");
        sites.Select(site => site.Client).Should().Contain(
            ChannelBackedClients,
            "a client missing here is one whose channel read the guard no longer examines");
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheReadAllLoopIsFollowedByTheCheckInTheShippedSpelling()
    {
        var source = Client(
            "await foreach (var item in ResultReader(channel.Reader).ReadAllAsync(ct).ConfigureAwait(false))",
            "    yield return item;",
            "ct.ThrowIfCancellationRequested();",
            "await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);");

        var violations = ChannelReadCancelScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
        ChannelReadCancelScanner.FindSites(source).Should().Equal(
            new ChannelReadSite("VendorSpeechRecognizer", BodyLine, ChannelReadCancelScanner.ReadAllShape));
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheWaitToReadLoopIsFollowedByTheCheck()
    {
        var source = Client(
            "while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))",
            "{",
            "    while (channel.Reader.TryRead(out var item))",
            "        yield return item;",
            "}",
            "ct.ThrowIfCancellationRequested();");

        var violations = ChannelReadCancelScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
        ChannelReadCancelScanner.FindSites(source).Should().ContainSingle()
            .Which.Shape.Should().Be(ChannelReadCancelScanner.WaitToReadShape);
    }

    [Fact]
    public void Scan_ShouldNameTheFileAndLine_WhenTheReadAllLoopHasNoCheck()
    {
        var source = Client(
            "await foreach (var item in ResultReader(channel.Reader).ReadAllAsync(ct).ConfigureAwait(false))",
            "    yield return item;",
            "await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);");

        var violations = ChannelReadCancelScanner.Scan(source, "src/Verbara.Sdk.VoiceAi.Stt/Fixture.cs");

        violations.Should().ContainSingle().Which.Should().Be(new ChannelReadCancelViolation(
            "src/Verbara.Sdk.VoiceAi.Stt/Fixture.cs", BodyLine, "VendorSpeechRecognizer",
            ChannelReadCancelScanner.ReadAllShape, "ct"));
    }

    [Theory]
    [MemberData(nameof(MisplacedChecks))]
    public void Scan_ShouldFlag_WhenTheCheckIsMissingOrMisplaced(string shape, string[] body)
    {
        var source = Client(body);

        var violations = ChannelReadCancelScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Shape.Should().Be(shape);
    }

    /// <summary>Each way of getting the check wrong, in each of the two shapes.</summary>
    public static TheoryData<string, string[]> MisplacedChecks()
    {
        const string readAll = "await foreach (var item in ResultReader(channel.Reader).ReadAllAsync(ct).ConfigureAwait(false))";
        const string readAllBody = "    yield return item;";
        const string waitToRead = "while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))";
        const string waitToReadBody = "    while (channel.Reader.TryRead(out var item)) yield return item;";
        const string whenAll = "await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);";
        const string check = "ct.ThrowIfCancellationRequested();";
        const string otherCheck = "other.ThrowIfCancellationRequested();";

        var data = new TheoryData<string, string[]>();
        foreach (var (shape, loop, loopBody) in new[]
        {
            (ChannelReadCancelScanner.ReadAllShape, readAll, readAllBody),
            (ChannelReadCancelScanner.WaitToReadShape, waitToRead, waitToReadBody),
        })
        {
            // No check at all.
            data.Add(shape, [loop, loopBody, whenAll]);

            // A check before the loop only.
            data.Add(shape, [check, loop, loopBody, whenAll]);

            // A check on another token.
            data.Add(shape, [loop, loopBody, otherCheck, whenAll]);

            // A check after Task.WhenAll, which awaits the send loop before the cancel is honoured.
            data.Add(shape, [loop, loopBody, whenAll, check]);
        }

        return data;
    }

    [Fact]
    public void Scan_ShouldReadTheTokenThroughWithCancellation_WhenReadAllAsyncIsGivenNone()
    {
        var source = Client(
            "await foreach (var item in channel.Reader.ReadAllAsync().WithCancellation(ct).ConfigureAwait(false))",
            "    yield return item;",
            "ct.ThrowIfCancellationRequested();");
        var wrongToken = Client(
            "await foreach (var item in channel.Reader.ReadAllAsync().WithCancellation(ct).ConfigureAwait(false))",
            "    yield return item;",
            "other.ThrowIfCancellationRequested();");

        ChannelReadCancelScanner.Scan(source, "x.cs").Should().BeEmpty();
        ChannelReadCancelScanner.Scan(wrongToken, "x.cs").Should().ContainSingle().Which.Token.Should().Be("ct");
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheReadIsGivenNoToken()
    {
        var source = Client(
            "await foreach (var item in channel.Reader.ReadAllAsync())",
            "    yield return item;",
            "ct.ThrowIfCancellationRequested();");

        var violations = ChannelReadCancelScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Token.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenTheWaitIsOneConditionOfSeveral()
    {
        var source = Client(
            "while (!stopped && await channel.Reader.WaitToReadAsync(ct))",
            "    while (channel.Reader.TryRead(out var item)) yield return item;",
            "await Task.WhenAll(sendTask, receiveTask);");

        var violations = ChannelReadCancelScanner.Scan(source, "x.cs");

        violations.Should().ContainSingle().Which.Shape.Should().Be(ChannelReadCancelScanner.WaitToReadShape);
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheTypeIsNotASpeechClientOrIsAbstract()
    {
        const string loop =
            "        await foreach (var item in channel.Reader.ReadAllAsync(ct)) { }\n";
        var source =
            "namespace Fixture;\n" +
            "public sealed class EventPump\n" +
            "{\n" +
            "    async Task M(Channel<int> channel, CancellationToken ct)\n" +
            "    {\n" + loop + "    }\n" +
            "}\n" +
            "public abstract class VendorSynthesizerBase : SpeechSynthesizer\n" +
            "{\n" +
            "    async Task M(Channel<int> channel, CancellationToken ct)\n" +
            "    {\n" + loop + "    }\n" +
            "}\n";

        ChannelReadCancelScanner.Scan(source, "x.cs").Should().BeEmpty();
        ChannelReadCancelScanner.FindSites(source).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenTheLoopEnumeratesSomethingOtherThanAChannelRead()
    {
        // The caller's audio, an IAsyncEnumerable, is not a channel a receive loop completes.
        var source = Client(
            "await foreach (var frame in audioFrames.WithCancellation(ct).ConfigureAwait(false))",
            "    yield return frame;");

        ChannelReadCancelScanner.Scan(source, "x.cs").Should().BeEmpty();
        ChannelReadCancelScanner.FindSites(source).Should().BeEmpty();
    }

    // The fixture's first body line, where a loop placed first in the body sits.
    private const int BodyLine = 6;

    /// <summary>A recognizer whose iterator body is <paramref name="body"/>, starting on <see cref="BodyLine"/>.</summary>
    private static string Client(params string[] body)
    {
        var sb = new StringBuilder();
        sb.Append("namespace Fixture;\n")
            .Append("public sealed class VendorSpeechRecognizer : SpeechRecognizer\n")
            .Append("{\n")
            .Append("    async IAsyncEnumerable<int> M(Channel<int> channel, CancellationToken ct, CancellationToken other)\n")
            .Append("    {\n");
        foreach (var line in body)
            sb.Append("        ").Append(line).Append('\n');

        sb.Append("    }\n")
            .Append("}\n");
        return sb.ToString();
    }

    private static IEnumerable<(string FullPath, string RelativePath)> SrcSources()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return SrcTreeSource.EnumerateSrcSources()
            .Select(file => (FullPath: file, RelativePath: Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/')));
    }

    private static string BuildFailureMessage(List<ChannelReadCancelViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" channel read(s) in speech clients not followed by a re-check of their token:");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  ").Append(v.Client).Append(": ").Append(v.Shape)
                .Append(v.Token.Length == 0
                    ? " is given no token, so there is nothing to re-check"
                    : $" is not followed by `{v.Token}.ThrowIfCancellationRequested();`")
                .AppendLine();
        }

        sb.AppendLine(
            "A channel reader checks its token before it reads the channel's state. A cancel landing between " +
            "the two, while the receive loop ends the channel cleanly in reaction to it, reaches the caller as " +
            "a normal end of stream, or as an empty result, instead of OperationCanceledException.");
        sb.AppendLine(
            "Put the check as the very next statement after the loop: before Task.WhenAll (which would first " +
            "await the send loop) and before any empty-result rule.");
        return sb.ToString();
    }
}
