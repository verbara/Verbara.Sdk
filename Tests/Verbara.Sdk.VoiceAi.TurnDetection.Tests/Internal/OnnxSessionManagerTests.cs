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
}
