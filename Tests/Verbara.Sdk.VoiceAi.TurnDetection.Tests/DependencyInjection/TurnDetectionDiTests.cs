using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.TurnDetection;
using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Sdk.VoiceAi.TurnDetection.Tests.DependencyInjection;

public sealed class TurnDetectionDiTests
{
    [Fact]
    public void AddSmartTurnDetection_ShouldRegisterSmartTurnDetector()
    {
        var services = new ServiceCollection();
        services.AddSmartTurnDetection();

        using var sp = services.BuildServiceProvider();
        var detector = sp.GetRequiredService<ITurnDetector>();
        detector.Should().BeOfType<SmartTurnDetector>();
    }

    [Fact]
    public void AddSmartTurnDetection_ShouldAcceptConfiguration()
    {
        var services = new ServiceCollection();
        services.AddSmartTurnDetection(opts =>
        {
            opts.TurnConfidenceThreshold = 0.7f;
            opts.IntraOpThreads = 2;
        });

        using var sp = services.BuildServiceProvider();
        var detector = sp.GetRequiredService<ITurnDetector>();
        detector.Should().BeOfType<SmartTurnDetector>();
    }

    [Fact]
    public void AddSmartTurnDetection_ShouldShareOnnxSessionAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSmartTurnDetection();

        using var sp = services.BuildServiceProvider();
        using var scope1 = sp.CreateScope();
        using var scope2 = sp.CreateScope();

        var d1 = scope1.ServiceProvider.GetRequiredService<ITurnDetector>();
        var d2 = scope2.ServiceProvider.GetRequiredService<ITurnDetector>();
        d1.Should().NotBeSameAs(d2, "detectors should be transient (one per session)");

        // Both should share the same OnnxSessionManager singleton
        var session1 = scope1.ServiceProvider.GetRequiredService<OnnxSessionManager>();
        var session2 = scope2.ServiceProvider.GetRequiredService<OnnxSessionManager>();
        session1.Should().BeSameAs(session2, "OnnxSessionManager should be singleton");
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void AddSmartTurnDetection_ShouldRejectInvalidTurnConfidenceThreshold(float threshold)
    {
        var services = new ServiceCollection();
        services.AddSmartTurnDetection(opts => opts.TurnConfidenceThreshold = threshold);

        using var sp = services.BuildServiceProvider();
        var act = () => sp.GetRequiredService<IOptions<SmartTurnDetectorOptions>>().Value;
        act.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData(-101.0)]
    [InlineData(0.1)]
    public void AddSmartTurnDetection_ShouldRejectInvalidSilenceThresholdDb(double threshold)
    {
        var services = new ServiceCollection();
        services.AddSmartTurnDetection(opts => opts.SilenceThresholdDb = threshold);

        using var sp = services.BuildServiceProvider();
        var act = () => sp.GetRequiredService<IOptions<SmartTurnDetectorOptions>>().Value;
        act.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void AddSmartTurnDetection_ShouldRejectInvalidIntraOpThreads(int threads)
    {
        var services = new ServiceCollection();
        services.AddSmartTurnDetection(opts => opts.IntraOpThreads = threads);

        using var sp = services.BuildServiceProvider();
        var act = () => sp.GetRequiredService<IOptions<SmartTurnDetectorOptions>>().Value;
        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddSmartTurnDetection_ShouldAcceptBoundaryValues()
    {
        var services = new ServiceCollection();
        services.AddSmartTurnDetection(opts =>
        {
            opts.TurnConfidenceThreshold = 0.0f;
            opts.SilenceThresholdDb = -100.0;
            opts.IntraOpThreads = 1;
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<SmartTurnDetectorOptions>>().Value;
        opts.TurnConfidenceThreshold.Should().Be(0.0f);
        opts.SilenceThresholdDb.Should().Be(-100.0);
        opts.IntraOpThreads.Should().Be(1);
    }
}
