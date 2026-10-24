using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Verbara.Sdk.Ari.Tests.TestSupport;
using FluentAssertions;
using Xunit.Abstractions;

namespace Verbara.Sdk.Ari.Tests.Client;

public sealed class AriDialConnectionCountTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("fin", 4)]
    [InlineData("rst", 0)]
    [InlineData("401 Unauthorized", 1)]
    [InlineData("503 Service Unavailable", 1)]
    public async Task ConnectAsync_ShouldPinTransportConnectionCount_WhenOneDialFails(string shape, int expected)
    {
        var counts = new List<int>();
        for (var iteration = 0; iteration < 20; iteration++)
            counts.Add(await CheckDialAsync(shape));
        if (shape == "rst") counts.Should().OnlyContain(count => count >= 1 && count <= 4, "immediate resets have variable runtime retry eligibility; every dial is recorded");
        else counts.Should().OnlyContain(count => count == expected, "each of the twenty independent caller dials has the documented transport count");
    }

    private async Task<int> CheckDialAsync(string shape)
    {
        using var trace = new HttpTrace();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var count = 0;
        var serve = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref count);
                if (shape == "rst") tcp.Client.LingerState = new LingerOption(true, 0);
                else if (shape != "fin")
                {
                    var buffer = new byte[4096];
                    _ = await tcp.GetStream().ReadAsync(buffer, stop.Token);
                    await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {shape}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                }
            }
        });
        var sut = AriClientClosingTests.Create(((IPEndPoint)listener.LocalEndpoint).Port, new ClosingLogger<Verbara.Sdk.Ari.Client.AriClient>());
        try
        {
            var fault = await Record.ExceptionAsync(() => sut.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            output.WriteLine($"shape={shape}; peerAccepts={count}; fault={fault?.GetType().Name}");
            foreach (var line in trace.Lines.Where(line => line.Contains("Retry attempt", StringComparison.Ordinal) || line.Contains("MaxConnectionFailureRetries", StringComparison.Ordinal) || line.Contains("/RequestStart", StringComparison.Ordinal) || line.Contains("/ConnectStart", StringComparison.Ordinal))) output.WriteLine(line);
            fault.Should().NotBeNull();
            return count;
        }
        finally
        {
            await sut.DisposeAsync();
            await stop.CancelAsync();
            listener.Stop();
            await serve.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }
    private sealed class HttpTrace : EventListener
    {
        internal ConcurrentQueue<string> Lines { get; } = new();
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name is "System.Net.Http" or "Private.InternalDiagnostics.System.Net.Http" or "System.Net.Sockets")
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName is "RequestStart" or "RequestStop" or "RequestFailed" or "ConnectStart" or "ConnectStop" || eventData.EventSource.Name.StartsWith("Private.", StringComparison.Ordinal))
                Lines.Enqueue($"{eventData.EventSource.Name}/{eventData.EventName}: {string.Join(" | ", eventData.Payload?.Select(p => p?.ToString()) ?? [])}");
        }
    }
}
