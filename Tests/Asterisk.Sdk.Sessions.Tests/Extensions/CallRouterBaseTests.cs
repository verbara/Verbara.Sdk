using Asterisk.Sdk.Sessions;
using Asterisk.Sdk.Sessions.Extensions;
using FluentAssertions;

namespace Asterisk.Sdk.Sessions.Tests.Extensions;

public sealed class CallRouterBaseTests
{
    [Fact]
    public async Task SelectNodeForOriginateAsync_ShouldDelegateToSelectNodeAsync()
    {
        var router = new TestCallRouter("node-1");

        var result = await router.SelectNodeForOriginateAsync(
            queueName: "sales",
            phoneNumber: "5551234",
            metadata: null,
            ct: CancellationToken.None);

        result.Should().Be("node-1");
    }

    [Fact]
    public async Task CanRouteAsync_ShouldReturnTrue_ByDefault()
    {
        var router = new TestCallRouter("node-1");

        var result = await router.CanRouteAsync(
            new CallSession("test", "linked-1", "server-1", CallDirection.Outbound),
            CancellationToken.None);

        result.Should().BeTrue();
    }

    private sealed class TestCallRouter : CallRouterBase
    {
        private readonly string _nodeId;

        public TestCallRouter(string nodeId) => _nodeId = nodeId;

        public override ValueTask<string> SelectNodeAsync(CallSession session, CancellationToken ct)
            => ValueTask.FromResult(_nodeId);
    }
}
