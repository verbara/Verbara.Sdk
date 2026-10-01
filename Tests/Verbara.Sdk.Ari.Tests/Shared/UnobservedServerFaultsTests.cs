using System.Runtime.CompilerServices;
using Verbara.Sdk.Tests.Shared.Sockets;
using FluentAssertions;

// Namespace, not folder: "Shared" is a reserved word in VB, and CA1716 rejects it in a namespace
// that declares public types.
namespace Verbara.Sdk.Ari.Tests.SharedSockets;

/// <summary>
/// Pins the shared <see cref="UnobservedServerFaults"/> recorder: <see cref="UnobservedServerFaults.Faults"/>
/// keeps only the faults whose stack names the type given to the constructor, and
/// <see cref="UnobservedServerFaults.All"/> keeps the others as well.
/// </summary>
public sealed class UnobservedServerFaultsTests
{
    [Fact]
    public void Faults_ShouldKeepOnlyTheGivenTypesFaults_WhileAllKeepsTheOthers()
    {
        var named = $"named-{Guid.NewGuid():N}";
        var other = $"other-{Guid.NewGuid():N}";
        using var recorder = new UnobservedServerFaults(nameof(FaultingFakeServer));

        DiscardFaultedTask(FaultingFakeServer.Fail(named));
        DiscardFaultedTask(UnrelatedComponent.Fail(other));
        UnobservedServerFaults.CollectDiscardedTasks();

        recorder.Faults.Should().ContainSingle(f => f.Contains(named, StringComparison.Ordinal))
            .And.NotContain(f => f.Contains(other, StringComparison.Ordinal),
                "a fault whose stack does not name the given type is filtered out");
        recorder.All.Should().Contain(f => f.Contains(named, StringComparison.Ordinal))
            .And.Contain(f => f.Contains(other, StringComparison.Ordinal), "All keeps every fault, for the failure message");
    }

    [Fact]
    public void Constructor_ShouldReject_WhenTheTypeNameIsEmpty()
    {
        var act = () => new UnobservedServerFaults(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>Drops the only reference to a faulted task, so the next collection publishes it as unobserved.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DiscardFaultedTask(Exception exception) => _ = Task.FromException(exception);

    private static class FaultingFakeServer
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static InvalidOperationException Fail(string message)
        {
            try
            {
                throw new InvalidOperationException(message);
            }
            catch (InvalidOperationException ex)
            {
                return ex;
            }
        }
    }

    private static class UnrelatedComponent
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static InvalidOperationException Fail(string message)
        {
            try
            {
                throw new InvalidOperationException(message);
            }
            catch (InvalidOperationException ex)
            {
                return ex;
            }
        }
    }
}
