namespace Verbara.Sdk.FunctionalTests.Layer2_UnitProtocol.StatusWire;

using System.Globalization;
using System.IO.Pipelines;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Ami.Generated;
using Verbara.Sdk.Ami.Internal;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.TestInfrastructure.Wire;

/// <summary>
/// From the bytes four supported Asterisk versions sent to <c>Action: Status</c> to the channel table: each captured
/// reply is parsed by the production reader and deserializer, handed to a <see cref="VerbaraServer"/> as its load's
/// <c>Status</c> answer, and every admitted channel is compared with the headers of its frame. No Docker; the
/// frames are the capture.
/// </summary>
public sealed class StatusSnapshotWireToTableTests
{
    public static TheoryData<string> Versions()
    {
        var data = new TheoryData<string>();
        foreach (var version in AmiStatusWireCapture.Versions)
            data.Add(version);
        return data;
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task RequestInitialStateAsync_ShouldAdmitEveryChannelAtTheHeadersItsFrameCarries_WhenTheSnapshotIsACapturedReply(
        string version)
    {
        var frames = await ParseAsync(AmiStatusWireCapture.StatusReply(version));
        frames.Should().HaveCount(AmiStatusWireCapture.StatusFramesPerVersion);

        var connection = Substitute.For<IAmiConnection>();
        connection.AsteriskVersion.Returns(version);
        // Answered by action type, never by call order: the load sends Status, then the bridge and conference
        // listings, then the queues and the agents. Only Status carries data here.
        connection
            .SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(ci => Reply(ci.ArgAt<ManagerAction>(0), frames));

        await using var server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
        await server.RequestInitialStateAsync();

        server.Channels.ActiveChannels.Should().HaveCount(frames.Count, $"Asterisk {version} listed six legs");
        using var scope = new AssertionScope();
        foreach (var frame in frames)
        {
            var channel = server.Channels.GetByUniqueId(frame.UniqueId!);
            channel.Should().NotBeNull($"{frame.Channel} is in the snapshot");
            if (channel is null)
                continue;

            var at = $"{frame.Channel} on Asterisk {version}";
            ((int)channel.State).ToString(CultureInfo.InvariantCulture).Should().Be(Header(frame, "ChannelState"), at);
            channel.CallerIdNum.Should().Be(Header(frame, "CallerIDNum"), at);
            channel.Context.Should().Be(Header(frame, "Context"), at);
            channel.Extension.Should().Be(Header(frame, "Exten"), at);
            channel.Priority.ToString(CultureInfo.InvariantCulture).Should().Be(Header(frame, "Priority"), at);
        }
    }

    private static string? Header(StatusEvent frame, string name) => frame.RawFields?.GetValueOrDefault(name);

    private static async IAsyncEnumerable<ManagerEvent> Reply(ManagerAction action, IReadOnlyList<StatusEvent> frames)
    {
        await Task.Yield();
        if (action is not StatusAction)
            yield break;

        foreach (var frame in frames)
            yield return frame;
    }

    /// <summary>The capture's <see cref="StatusEvent"/>s, parsed as <c>AmiConnection</c>'s reader loop parses them.</summary>
    private static async Task<IReadOnlyList<StatusEvent>> ParseAsync(byte[] capture)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(capture);
        await pipe.Writer.CompleteAsync();

        var reader = new AmiProtocolReader(pipe.Reader);
        var statuses = new List<StatusEvent>();
        while (await reader.ReadMessageAsync() is { } msg)
        {
            if (msg.IsEvent && GeneratedEventDeserializer.Deserialize(msg) is StatusEvent status)
                statuses.Add(status);
        }

        return statuses;
    }
}
