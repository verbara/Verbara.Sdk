using System.Text;
using System.Text.Json;
using Verbara.Sdk.VoiceAi.Tts.Deepgram;
using Verbara.Sdk.VoiceAi.Tts.Lmnt;
using Verbara.Sdk.VoiceAi.Tts.Speechmatics;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.Tts.Tests.VoiceCatalog;

/// <summary>
/// Checks every voice identifier this SDK publishes against the vendor that has to accept it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The three catalogs were transcribed from vendor documentation in May 2026
/// and nothing re-read them afterwards. By August two entries had rotted: the Speechmatics default
/// <c>eleanor</c> was not a voice at all, and <c>DeepgramVoices.Helios</c> carried
/// <c>aura-2-helios-en</c>, an id the API rejects — there is no Aura 2 Helios. Neither was a
/// mistake anyone could have caught by reading the code, because nothing in the build compares a
/// string constant to the roster of the service that receives it. That is the gap this class
/// closes; without it the next stale id waits until a customer hears the wrong voice.
/// </para>
/// <para>
/// Tagged <c>Realtime</c>, so the unit lane skips it, and gated on credentials, so it skips rather
/// than fails when unconfigured.
/// </para>
/// </remarks>
[Trait("Category", "Realtime")]
public sealed class VoiceCatalogConformanceTests
{
    private const string Sentence = "The quick brown fox jumps over the lazy dog.";

    private static HttpClient NewClient() =>
        new() { Timeout = TimeSpan.FromSeconds(60) };

    private static string Key(string name) =>
        Environment.GetEnvironmentVariable(name) ?? string.Empty;

    // ── LMNT — the vendor publishes a list, so membership is the assertion ────────────────────

    [RequiresVendorCredentialFact("LMNT_API_KEY")]
    public async Task LmntVoices_ShouldAllAppearInTheVendorRoster_WhenListedLive()
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://api.lmnt.com/v1/ai/voice/list?owner=system");
        request.Headers.TryAddWithoutValidation("X-API-Key", Key("LMNT_API_KEY"));

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var live = await ReadVoiceIdsAsync(response, "voices", "id");
        live.Should().NotBeEmpty("the roster is the yardstick — an empty one proves nothing");

        foreach (var shipped in ShippedConstants(typeof(LmntVoices)))
        {
            live.Should().Contain(shipped,
                $"LmntVoices publishes '{shipped}', so the vendor must still accept it");
        }
    }

    // ── Deepgram — same shape, plus the id that was actually broken ───────────────────────────

    [RequiresVendorCredentialFact("DEEPGRAM_API_KEY")]
    public async Task DeepgramVoices_ShouldAllAppearInTheVendorRoster_WhenListedLive()
    {
        using var client = NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepgram.com/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {Key("DEEPGRAM_API_KEY")}");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var live = await ReadVoiceIdsAsync(response, "tts", "canonical_name");
        live.Should().NotBeEmpty();

        foreach (var shipped in ShippedConstants(typeof(DeepgramVoices)))
        {
            live.Should().Contain(shipped,
                $"DeepgramVoices publishes '{shipped}', so the vendor must still serve it");
        }

        live.Should().NotContain("aura-2-helios-en",
            "the id this catalog shipped until 2026-08-18 — if the vendor ever adds it, "
            + "revisit HeliosLegacy rather than assuming the old constant was right all along");
    }

    // ── Speechmatics — no usable roster, so the speaker itself is the assertion ───────────────

    /// <summary>
    /// The Speechmatics preview cannot be checked by asking it what it offers. Its
    /// <c>GET /voices</c> under-reports (it named one voice while three others demonstrably
    /// worked), and <c>POST /generate/{anything}</c> answers <c>200 audio/wav</c> for every
    /// segment, valid or not. The only thing that separates a real voice from a typo is who is
    /// speaking — so this test measures that directly.
    /// </summary>
    /// <remarks>
    /// Byte comparison is useless here: synthesis is stochastic, and one voice asked twice for one
    /// sentence returns different audio of different length. Median fundamental frequency is
    /// stable across runs (spread under 5 Hz) while the catalog's speakers sit 20-107 Hz apart, so
    /// F0 both discriminates and carries its own noise floor.
    /// </remarks>
    [RequiresVendorCredentialFact("SPEECHMATICS_API_KEY")]
    public async Task SpeechmaticsVoices_ShouldEachBeADistinctSpeaker_AndAnUnknownVoiceShouldFallBack()
    {
        using var client = NewClient();

        var pitch = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var voice in SpeechmaticsVoices.All)
        {
            pitch[voice] = MedianF0(await SynthesizeAsync(client, voice));
        }

        // Positive control: the catalog claims four speakers. If the path segment were ignored they
        // would collapse into one cluster, and every assertion below would be measuring nothing.
        var ordered = pitch.Values.Order().ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            (ordered[i] - ordered[i - 1]).Should().BeGreaterThan(10d,
                "each catalog voice must be a separate speaker — neighbouring medians "
                + $"{ordered[i - 1]:F1} Hz and {ordered[i]:F1} Hz are within measurement noise");
        }

        // Negative control: an unrecognised segment does not fail, it falls back. Measured on
        // 2026-08-18 the fallback speaker is Jack, which is why Jack is the shipped default.
        var fallback = MedianF0(await SynthesizeAsync(client, "zzqqxx-not-a-voice"));
        fallback.Should().BeApproximately(pitch[SpeechmaticsVoices.Jack], 15d,
            "an unknown voice is answered in the fallback speaker's voice, with a 200 status and "
            + "nothing in the response to say the request was not honoured");

        // And the reason the option validator is case-sensitive.
        var miscased = MedianF0(await SynthesizeAsync(client, "Sarah"));
        miscased.Should().BeApproximately(pitch[SpeechmaticsVoices.Jack], 15d,
            "'Sarah' is not 'sarah' to this API — it falls back silently, so case-insensitive "
            + "validation would wave through a value the service ignores");
    }

    [RequiresVendorCredentialFact("SPEECHMATICS_API_KEY")]
    public async Task SpeechmaticsVoices_ShouldNotContainEleanor_WhenMeasuredAgainstTheFallback()
    {
        using var client = NewClient();

        var jack = MedianF0(await SynthesizeAsync(client, SpeechmaticsVoices.Jack));
        var eleanor = MedianF0(await SynthesizeAsync(client, "eleanor"));

        SpeechmaticsVoices.IsKnown("eleanor").Should().BeFalse(
            "'eleanor' shipped as the default until 2026-08-18 and is not a voice");
        eleanor.Should().BeApproximately(jack, 15d,
            "the regression pin: 'eleanor' returns 200 with audio, which is exactly why the "
            + "status code could never have caught this — only the speaker can");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static async Task<byte[]> SynthesizeAsync(HttpClient client, string voice)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://preview.tts.speechmatics.com/generate/{Uri.EscapeDataString(voice)}")
        {
            Content = new StringContent(
                $"{{\"text\":\"{Sentence}\"}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization", $"Bearer {Key("SPEECHMATICS_API_KEY")}");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Reads <c>root[collection][field]</c> out of a JSON list response.</summary>
    private static async Task<HashSet<string>> ReadVoiceIdsAsync(
        HttpResponseMessage response, string collection, string field)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.GetProperty(collection);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            if (element.TryGetProperty(field, out var value) &&
                value.GetString() is { Length: > 0 } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Every <c>public const string</c> the catalog declares. Read from the compiled metadata so a
    /// constant added tomorrow is covered without anyone remembering to list it here — which is
    /// the whole point, since forgetting is how the stale ids survived.
    /// </summary>
    private static IEnumerable<string> ShippedConstants(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)]
        Type catalog) =>
        catalog
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue())
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!);

    /// <summary>
    /// Median fundamental frequency over voiced 40 ms frames of a 16 kHz mono PCM WAV, by
    /// autocorrelation. Frames quieter than 35% of overall RMS are silence; frames whose best lag
    /// correlates below 0.35 are unvoiced. Both are dropped rather than averaged in.
    /// </summary>
    private static double MedianF0(byte[] wav)
    {
        const int SampleRate = 16000;
        const int HeaderBytes = 44;

        var sampleCount = (wav.Length - HeaderBytes) / 2;
        var samples = new double[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = BitConverter.ToInt16(wav, HeaderBytes + (i * 2));
        }

        var frame = (int)(0.040 * SampleRate);
        var hop = frame / 2;
        var minLag = SampleRate / 350;   // 350 Hz ceiling
        var maxLag = SampleRate / 60;    // 60 Hz floor

        var overallRms = Rms(samples, 0, samples.Length);
        var pitches = new List<double>();

        for (var start = 0; start + frame < samples.Length; start += hop)
        {
            if (Rms(samples, start, frame) < 0.35 * overallRms)
            {
                continue;
            }

            var mean = 0d;
            for (var i = 0; i < frame; i++)
            {
                mean += samples[start + i];
            }

            mean /= frame;

            var energy = 0d;
            for (var i = 0; i < frame; i++)
            {
                var v = samples[start + i] - mean;
                energy += v * v;
            }

            if (energy <= 0)
            {
                continue;
            }

            var bestLag = 0;
            var bestScore = double.NegativeInfinity;
            for (var lag = minLag; lag < maxLag && lag < frame; lag++)
            {
                var score = 0d;
                for (var i = 0; i + lag < frame; i++)
                {
                    score += (samples[start + i] - mean) * (samples[start + i + lag] - mean);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestLag = lag;
                }
            }

            if (bestLag > 0 && bestScore / energy >= 0.35)
            {
                pitches.Add((double)SampleRate / bestLag);
            }
        }

        pitches.Should().NotBeEmpty("a synthesis with no voiced frame is not audio worth measuring");
        pitches.Sort();
        return pitches[pitches.Count / 2];
    }

    private static double Rms(double[] samples, int offset, int count)
    {
        var sum = 0d;
        for (var i = 0; i < count; i++)
        {
            sum += samples[offset + i] * samples[offset + i];
        }

        return Math.Sqrt(sum / count);
    }
}
