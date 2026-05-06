using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.TurnDetection;
using Verbara.Sdk.VoiceAi.TurnDetection.Internal;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
}
