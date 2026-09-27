using System.Text;

namespace Verbara.Sdk.Governance.Tests;

/// <summary>
/// In-process guard for "every WebSocket speech client closes the three failure doors" (openspec change
/// <c>a-decision-is-held-by-a-test-that-can-fail</c>; ADR-0049 D1, ADR-0050 E2 and E7): every
/// non-abstract synthesizer or recognizer in <c>src/Verbara.Sdk.VoiceAi.Tts</c> and
/// <c>src/Verbara.Sdk.VoiceAi.Stt</c> declared in a file that references <c>ClientWebSocket</c> calls
/// <c>SpeechProviderFailureException.FromErrorFrame</c>, <c>FromCloseStatus</c>, <c>FromTransport</c>
/// and <c>FromHandshake</c>. Zero tolerance: all eight clients ADR-0050 names call all four today.
/// </summary>
/// <remarks>
/// <para>
/// The scope is those two packages and no other, by decision: PBX audio sessions classify the same
/// exception shapes the other way, as an ending rather than a failure (ADR-0053 R3), so a repo-wide
/// rule would be wrong for them.
/// </para>
/// <para>
/// The guard checks presence, not correctness. A client that calls <c>FromCloseStatus</c> on the
/// wrong branch still passes; that stays with each provider's own tests. What it catches is the
/// regression that shipped before ADR-0050: a door quietly removed, so a vendor's refusal ends the
/// stream empty and successful.
/// </para>
/// </remarks>
public sealed class SpeechClientDoorGuardTests
{
    // Conservative floor, below the two packages as this guard landed (36 source files).
    private const int MinimumScannedFiles = 25;

    /// <summary>
    /// The eight WebSocket clients ADR-0050 audited. The guard must recognise each of them, so a client
    /// that moved its socket into another file, and so dropped out of scope, fails here instead of
    /// passing unexamined.
    /// </summary>
    private static readonly string[] Adr0050WebSocketClients =
    [
        "CartesiaSpeechSynthesizer",
        "ElevenLabsSpeechSynthesizer",
        "LmntSpeechSynthesizer",
        "DeepgramSpeechSynthesizer",
        "SpeechmaticsSpeechRecognizer",
        "AssemblyAiSpeechRecognizer",
        "CartesiaSpeechRecognizer",
        "DeepgramSpeechRecognizer",
    ];

    private static readonly string[] SpeechPackageRoots =
    [
        "src/Verbara.Sdk.VoiceAi.Tts/",
        "src/Verbara.Sdk.VoiceAi.Stt/",
    ];

    [Fact]
    public void Guard_ShouldFindEveryDoorInEveryWebSocketClient_InTheSpeechPackages()
    {
        var violations = SpeechPackageSources()
            .SelectMany(file => SpeechClientDoorScanner.Scan(File.ReadAllText(file.FullPath), file.RelativePath))
            .ToList();

        violations.Should().BeEmpty(BuildFailureMessage(violations));
    }

    [Fact]
    public void Guard_ShouldScanManyFiles_WhenWalkingTheSpeechPackages()
    {
        var count = SpeechPackageSources().Count();

        count.Should().BeGreaterThan(
            MinimumScannedFiles,
            "the guard must walk the TTS and STT packages; a near-zero count means the locator or the " +
            "package filter broke and the doors check would be a false green");
    }

    [Fact]
    public void Guard_ShouldRecogniseTheEightWebSocketClients_WhenWalkingTheSpeechPackages()
    {
        var clients = SpeechPackageSources()
            .SelectMany(file => SpeechClientDoorScanner.FindWebSocketClients(File.ReadAllText(file.FullPath)))
            .ToList();

        clients.Should().Contain(
            Adr0050WebSocketClients,
            "each client ADR-0050 names holds a ClientWebSocket and must stay in the guard's scope; a " +
            "client missing here is one the doors check no longer examines");
    }

    [Fact]
    public void Scan_ShouldNameTheClientAndTheDoor_WhenAWebSocketRecognizerNoLongerReadsTheCloseCode()
    {
        // The spec's scenario: the close branch went back to a bare `break`.
        var source = WebSocketClient("DeepgramSpeechRecognizer", "SpeechRecognizer", "FromErrorFrame", "FromTransport", "FromHandshake");

        var violations = SpeechClientDoorScanner.Scan(source, "src/Verbara.Sdk.VoiceAi.Stt/Fixture.cs");

        violations.Should().ContainSingle().Which.Should().Be(new SpeechClientDoorViolation(
            "src/Verbara.Sdk.VoiceAi.Stt/Fixture.cs", 3, "DeepgramSpeechRecognizer", "FromCloseStatus"));
    }

    [Fact]
    public void Scan_ShouldReportEveryDoor_WhenAWebSocketSynthesizerCallsNone()
    {
        var source = WebSocketClient("VendorSpeechSynthesizer", "SpeechSynthesizer");

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Select(v => v.Door).Should().Equal(SpeechClientDoorScanner.Doors);
        violations.Should().OnlyContain(v => v.Client == "VendorSpeechSynthesizer");
    }

    [Fact]
    public void Scan_ShouldPass_WhenAWebSocketClientCallsAllFourDoors()
    {
        var source = WebSocketClient("VendorSpeechRecognizer", "SpeechRecognizer", [.. SpeechClientDoorScanner.Doors]);

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
        SpeechClientDoorScanner.FindWebSocketClients(source).Should().Equal("VendorSpeechRecognizer");
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheFileDoesNotReferenceClientWebSocket()
    {
        // An HTTP client (Azure TTS, Google STT, Whisper) has no receive loop and no doors to close.
        const string source =
            "namespace Fixture;\n" +
            "public sealed class VendorSpeechSynthesizer : SpeechSynthesizer\n" +
            "{\n" +
            "    private readonly System.Net.Http.HttpClient _http = new();\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
        SpeechClientDoorScanner.FindWebSocketClients(source).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldPass_WhenClientWebSocketIsOnlyNamedInACommentOrAString()
    {
        const string source =
            "namespace Fixture;\n" +
            "/// <summary>Unlike the ClientWebSocket clients, this one posts over HTTP.</summary>\n" +
            "public sealed class VendorSpeechSynthesizer : SpeechSynthesizer\n" +
            "{\n" +
            "    // no ClientWebSocket here\n" +
            "    private const string Note = \"ClientWebSocket\";\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheWebSocketSubclassIsAbstract()
    {
        const string source =
            "using System.Net.WebSockets;\n" +
            "namespace Fixture;\n" +
            "public abstract class WebSocketSynthesizerBase : SpeechSynthesizer\n" +
            "{\n" +
            "    protected ClientWebSocket? Socket;\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
        SpeechClientDoorScanner.FindWebSocketClients(source).Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldIgnore_WhenAWebSocketFileDeclaresATypeThatIsNotASpeechClient()
    {
        const string source =
            "using System.Net.WebSockets;\n" +
            "namespace Fixture;\n" +
            "internal sealed class SocketPump { private ClientWebSocket? _ws; }\n" +
            "internal sealed class VendorVoices : VoiceCatalog { }\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldRecogniseTheClient_WhenTheBaseAndTheSocketAreFullyQualified()
    {
        const string source =
            "namespace Fixture;\n" +
            "public sealed class VendorSpeechRecognizer : Verbara.Sdk.VoiceAi.SpeechRecognizer\n" +
            "{\n" +
            "    private System.Net.WebSockets.ClientWebSocket? _ws;\n" +
            "    void Fail(string p, System.Exception ex) {\n" +
            "        throw Verbara.Sdk.VoiceAi.SpeechProviderFailureException.FromTransport(p, ex);\n" +
            "    }\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Select(v => v.Door).Should().Equal("FromErrorFrame", "FromCloseStatus", "FromHandshake");
    }

    [Fact]
    public void Scan_ShouldCountADoor_WhenItIsCalledInsideALambdaOrALocalFunction()
    {
        const string source =
            "using System.Net.WebSockets;\n" +
            "namespace Fixture;\n" +
            "public sealed class VendorSpeechRecognizer : SpeechRecognizer\n" +
            "{\n" +
            "    private ClientWebSocket? _ws;\n" +
            "    void M(string p, System.Exception ex) {\n" +
            "        System.Func<System.Exception> a = () => SpeechProviderFailureException.FromErrorFrame(p, null, null);\n" +
            "        System.Exception B() => SpeechProviderFailureException.FromCloseStatus(p, null, null)!;\n" +
            "        _ = Task.Run(() => { throw SpeechProviderFailureException.FromTransport(p, ex); });\n" +
            "        throw SpeechProviderFailureException.FromHandshake(p, null, ex);\n" +
            "    }\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenADoorIsCalledOnAnotherTypeOrAsAnUnimportedHelper()
    {
        // A same-named helper of the client's own, or a factory on some other type, is not the typed
        // provider failure ADR-0050 E1 requires.
        const string source =
            "using System.Net.WebSockets;\n" +
            "namespace Fixture;\n" +
            "public sealed class VendorSpeechRecognizer : SpeechRecognizer\n" +
            "{\n" +
            "    private ClientWebSocket? _ws;\n" +
            "    void M(string p, System.Exception ex) {\n" +
            "        throw FromErrorFrame(p);\n" +
            "        throw OtherFailure.FromCloseStatus(p, null, null);\n" +
            "        throw SpeechProviderFailureException.FromTransport(p, ex);\n" +
            "        throw SpeechProviderFailureException.FromHandshake(p, null, ex);\n" +
            "    }\n" +
            "    static System.Exception FromErrorFrame(string p) => new(p);\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Select(v => v.Door).Should().Equal("FromErrorFrame", "FromCloseStatus");
    }

    [Fact]
    public void Scan_ShouldPass_WhenTheDoorsAreImportedWithUsingStatic()
    {
        const string source =
            "using System.Net.WebSockets;\n" +
            "using static Verbara.Sdk.VoiceAi.SpeechProviderFailureException;\n" +
            "namespace Fixture;\n" +
            "public sealed class VendorSpeechSynthesizer : SpeechSynthesizer\n" +
            "{\n" +
            "    private ClientWebSocket? _ws;\n" +
            "    void M(string p, System.Exception ex) {\n" +
            "        throw FromErrorFrame(p, null, null);\n" +
            "        throw FromCloseStatus(p, null, null)!;\n" +
            "        throw FromTransport(p, ex);\n" +
            "        throw FromHandshake(p, null, ex);\n" +
            "    }\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Scan_ShouldFlag_WhenADoorIsNamedOnlyInACommentOrAString()
    {
        const string source =
            "using System.Net.WebSockets;\n" +
            "namespace Fixture;\n" +
            "public sealed class VendorSpeechSynthesizer : SpeechSynthesizer\n" +
            "{\n" +
            "    private ClientWebSocket? _ws;\n" +
            "    void M(string p, System.Exception ex) {\n" +
            "        // throw SpeechProviderFailureException.FromErrorFrame(p, null, null);\n" +
            "        var s = \"SpeechProviderFailureException.FromCloseStatus(p, null, null)\";\n" +
            "        throw SpeechProviderFailureException.FromTransport(p, ex);\n" +
            "        throw SpeechProviderFailureException.FromHandshake(p, null, ex);\n" +
            "    }\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Select(v => v.Door).Should().Equal("FromErrorFrame", "FromCloseStatus");
    }

    [Fact]
    public void Scan_ShouldCountEachClientSeparately_WhenOneFileDeclaresTwo()
    {
        // A door called by one client does not close it for its neighbour.
        const string source =
            "using System.Net.WebSockets;\n" +
            "namespace Fixture;\n" +
            "public sealed class FirstSpeechRecognizer : SpeechRecognizer\n" +
            "{\n" +
            "    private ClientWebSocket? _ws;\n" +
            "    void M(string p, System.Exception ex) {\n" +
            "        throw SpeechProviderFailureException.FromErrorFrame(p, null, null);\n" +
            "        throw SpeechProviderFailureException.FromCloseStatus(p, null, null)!;\n" +
            "        throw SpeechProviderFailureException.FromTransport(p, ex);\n" +
            "        throw SpeechProviderFailureException.FromHandshake(p, null, ex);\n" +
            "    }\n" +
            "}\n" +
            "public sealed class SecondSpeechRecognizer : SpeechRecognizer\n" +
            "{\n" +
            "    void M(string p, System.Exception ex) {\n" +
            "        throw SpeechProviderFailureException.FromTransport(p, ex);\n" +
            "    }\n" +
            "}\n";

        var violations = SpeechClientDoorScanner.Scan(source, "x.cs");

        violations.Should().OnlyContain(v => v.Client == "SecondSpeechRecognizer" && v.Line == 13);
        violations.Select(v => v.Door).Should().Equal("FromErrorFrame", "FromCloseStatus", "FromHandshake");
    }

    /// <summary>
    /// A WebSocket client of <paramref name="baseType"/> that calls exactly <paramref name="doors"/>;
    /// its class declaration is on line 3.
    /// </summary>
    private static string WebSocketClient(string name, string baseType, params string[] doors)
    {
        var sb = new StringBuilder();
        sb.Append("using System.Net.WebSockets;\n")
            .Append("namespace Fixture;\n")
            .Append("public sealed class ").Append(name).Append(" : ").Append(baseType).Append('\n')
            .Append("{\n")
            .Append("    private static void Fail(ClientWebSocket ws, string provider, System.Exception ex)\n")
            .Append("    {\n");
        foreach (var door in doors)
            sb.Append("        throw SpeechProviderFailureException.").Append(door).Append("(provider, null, ex);\n");

        sb.Append("    }\n")
            .Append("}\n");
        return sb.ToString();
    }

    private static IEnumerable<(string FullPath, string RelativePath)> SpeechPackageSources()
    {
        var repoRoot = Directory.GetParent(SrcTreeSource.SrcRoot())!.FullName;
        return SrcTreeSource.EnumerateSrcSources()
            .Select(file => (FullPath: file, RelativePath: ToRelative(repoRoot, file)))
            .Where(file => SpeechPackageRoots.Any(root => file.RelativePath.StartsWith(root, StringComparison.Ordinal)));
    }

    private static string ToRelative(string repoRoot, string file) =>
        Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildFailureMessage(List<SpeechClientDoorViolation> violations)
    {
        var sb = new StringBuilder();
        sb.Append(violations.Count)
            .AppendLine(" door(s) missing from WebSocket speech clients (ADR-0049 D1, ADR-0050 E2 and E7):");
        foreach (var v in violations.OrderBy(v => v.Path, StringComparer.Ordinal).ThenBy(v => v.Line))
        {
            sb.Append("  ").Append(v.Path).Append(':').Append(v.Line)
                .Append("  ").Append(v.Client).Append(" never calls SpeechProviderFailureException.")
                .Append(v.Door).Append(" — ").AppendLine(DoorMeaning(v.Door));
        }

        sb.AppendLine(
            "Each door is a way a vendor's refusal leaves a WebSocket session. Left open, the refusal ends " +
            "the caller's stream empty and successful, which is the defect ADR-0049 and ADR-0050 were " +
            "written for.");
        sb.AppendLine(
            "This guard checks that each call is PRESENT in the client, not that it is correct: a call on " +
            "the wrong branch passes here, and each provider's own tests own that. A missing call means the " +
            "door was removed.");
        return sb.ToString();
    }

    private static string DoorMeaning(string door) => door switch
    {
        "FromErrorFrame" => "a vendor error frame must throw, not fall into a discard branch (ADR-0049 D1, ADR-0050 E2a).",
        "FromCloseStatus" => "a failure close code must throw, not end the stream (ADR-0050 E2b).",
        "FromTransport" => "a mid-stream WebSocketException must throw, not break (ADR-0050 E2c).",
        "FromHandshake" => "a rejected handshake must be wrapped in the typed failure (ADR-0050 E7).",
        _ => "an unknown door.",
    };
}
