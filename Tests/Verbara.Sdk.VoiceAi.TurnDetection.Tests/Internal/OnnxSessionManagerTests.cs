using System.Security.Cryptography;
using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using FluentAssertions;
using Xunit;

namespace Verbara.Sdk.VoiceAi.TurnDetection.Tests.Internal;

public sealed class OnnxSessionManagerTests
{
    [Fact]
    public void RunInference_ShouldReturnProbability_ForSilentInput()
    {
        using var manager = new OnnxSessionManager();
        var mel = new float[80 * 800]; // all zeros = silence
        var prob = manager.RunInference(mel);
        prob.Should().BeInRange(0f, 1f);
    }

    [Fact]
    public void RunInference_ShouldReturnDeterministicResult_ForSameInput()
    {
        using var manager = new OnnxSessionManager();
        var mel = new float[80 * 800]; // all zeros
        var prob1 = manager.RunInference(mel);
        var prob2 = manager.RunInference(mel);
        prob1.Should().Be(prob2, "identical input must produce identical output");
    }

    /// <summary>
    /// The pin leg of ADR-0042 D8: an ATTRIBUTED figure must be bound to the artifact actually
    /// shipped, so replacing the model breaks the build rather than silently orphaning the number.
    /// <c>README.md</c>'s accuracy figure is upstream's measurement of <c>smart-turn-v3.2-cpu</c>
    /// specifically, and this is what makes that citation checkable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hash is asserted, not just the length, and the reason is measurable: upstream's
    /// <c>v3.1-cpu</c> is 8 679 180 bytes against v3.2-cpu's 8 679 182 — a two-byte difference. A
    /// length-only pin would pass on a silent downgrade to a model whose published English accuracy
    /// is 90.66 % rather than 94.26 %.
    /// </para>
    /// <para>
    /// Read through the manifest stream rather than off disk, because that is the path
    /// <see cref="OnnxSessionManager"/> itself loads: the resource carries an explicit
    /// <c>LogicalName</c>, so there is no namespace prefix. If the checkout lacks Git LFS the stream
    /// is a ~130-byte pointer file and this test fails on the length assertion — which is the
    /// intended behaviour, not a false red.
    /// </para>
    /// </remarks>
    [Fact]
    public void EmbeddedModel_ShouldBeTheExactArtifactTheAccuracyClaimCites()
    {
        const string ResourceName = "smart-turn-v3.2-cpu.onnx";
        const int ExpectedLength = 8_679_182;
        const string ExpectedSha256 = "2bb026316b14a660486a75b1733cd3fbab8c2fd0314dc9af7be49f8cca967e4f";

        using var stream = typeof(OnnxSessionManager).Assembly.GetManifestResourceStream(ResourceName);

        stream.Should().NotBeNull(
            "the model ships as an embedded resource under an explicit LogicalName");

        using var buffer = new MemoryStream();
        stream!.CopyTo(buffer);
        var bytes = buffer.ToArray();

        bytes.Should().HaveCount(ExpectedLength,
            "a short read here means the checkout resolved the Git LFS pointer instead of the model");
        Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(ExpectedSha256,
            "the accuracy figure in README.md is upstream's measurement of this exact artifact");
    }
}
