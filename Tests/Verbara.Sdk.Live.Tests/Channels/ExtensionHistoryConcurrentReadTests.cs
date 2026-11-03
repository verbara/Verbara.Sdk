using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Channels;

namespace Verbara.Sdk.Live.Tests.Channels;

/// <summary>
/// A reader enumerates a channel's extension history on one thread while another reports visits for that channel:
/// no enumeration throws, and each list read is in order and holds at most 100 entries.
/// </summary>
public sealed class ExtensionHistoryConcurrentReadTests
{
    private const int Visits = 2_000;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void ExtensionHistory_ShouldReadAConsistentList_WhenAnotherThreadRecordsVisitsWhileItIsEnumerated()
    {
        var manager = new ChannelManager(NullLogger.Instance);
        manager.OnNewChannel("1.1", "PJSIP/a-1", ChannelState.Up, context: "ctx", exten: "s", priority: 1);
        var channel = manager.GetByUniqueId("1.1")!;
        using var started = new ManualResetEventSlim();
        var writing = 1;
        Exception? writerFailure = null;
        var writer = new Thread(() =>
        {
            try
            {
                started.Wait(Bound);
                for (var step = 1; step <= Visits; step++)
                    manager.OnNewExten("1.1", "ctx", "s", step);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                writerFailure = ex;
            }
            finally
            {
                Volatile.Write(ref writing, 0);
            }
        }) { IsBackground = true, Name = "extension-history-writer" };

        var reads = 0;
        var readerFailures = new List<string>();
        writer.Start();
        started.Set();
        var deadline = DateTime.UtcNow + Bound;
        while ((Volatile.Read(ref writing) == 1 || reads == 0) && DateTime.UtcNow < deadline)
        {
            try
            {
                var previous = 0;
                var count = 0;
                foreach (var entry in channel.ExtensionHistory)
                {
                    if (previous != 0 && entry.Priority != previous + 1)
                        readerFailures.Add($"out of order: {previous} then {entry.Priority}");
                    previous = entry.Priority;
                    count++;
                }

                if (count > 100)
                    readerFailures.Add($"{count} entries");
                reads++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                readerFailures.Add($"{ex.GetType().Name}: {ex.Message}");
                reads++;
            }
        }

        var joined = writer.Join(Bound);

        using var scope = new AssertionScope();
        joined.Should().BeTrue();
        writerFailure.Should().BeNull();
        reads.Should().BeGreaterThan(0);
        readerFailures.Should().BeEmpty("no enumeration throws and each list read is in order with at most 100 entries");
        channel.ExtensionHistory.Should().HaveCount(100);
    }
}
