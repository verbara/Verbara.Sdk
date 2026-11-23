using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Sdk.Ami.Events;
using Verbara.Sdk.Live.MeetMe;
using Verbara.Sdk.Live.Server;

// These tests name the former MeetMe events on purpose: they hold them to the removal a 2.10.x binary relied on.
#pragma warning disable VSDK0008

namespace Verbara.Sdk.Live.Tests.Conferences;

/// <summary>
/// Removing a delegate from a former <c>MeetMeManager</c> event gives what the field-like event of 2.10.x gave, which is
/// <see cref="Delegate.Remove(Delegate, Delegate)"/>: a delegate with several handlers is removed only as the last
/// contiguous run of the event's list, and an absent run removes nothing. Each case drives the manager and a local
/// field-like event with the same operations and compares the handlers each one invokes, in order.
/// </summary>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed via IAsyncLifetime")]
public sealed class ConferenceFormerNamesRemovalTests : IAsyncLifetime
{
    private const string Room = "100";
    private const string Channel = "Local/conf@probe-00000000;2";

    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(30);

    private readonly VerbaraServer _server;
    private IObserver<ManagerEvent>? _observer;

    public ConferenceFormerNamesRemovalTests()
    {
        var connection = Substitute.For<IAmiConnection>();
        connection.Subscribe(Arg.Do<IObserver<ManagerEvent>>(obs => _observer = obs)).Returns(Substitute.For<IDisposable>());
        connection.SendEventGeneratingActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>()).Returns(Empty());
        _server = new VerbaraServer(connection, NullLogger<VerbaraServer>.Instance);
    }

    public enum FormerEvent
    {
        UserJoined,
        UserLeft,
        UserStateChanged,
    }

    // Operations separated by spaces: "+A+B" subscribes A+B as one delegate, "-A+C" removes A+C as one delegate.
    public static TheoryData<FormerEvent, string, string> Cases
    {
        get
        {
            var data = new TheoryData<FormerEvent, string, string>();
            foreach (var formerEvent in Enum.GetValues<FormerEvent>())
            {
                data.Add(formerEvent, "+A +B +C -A+C", "A B C");
                data.Add(formerEvent, "+A +B +C -B+C", "A");
                data.Add(formerEvent, "+A+B +A+B -A+B", "A B");
                data.Add(formerEvent, "+A +A -A -A", "");
                data.Add(formerEvent, "+A +B -C", "A B");
                data.Add(formerEvent, "+A +B +C +A +B -A+B", "A B C");
            }

            return data;
        }
    }

    public Task InitializeAsync() => _server.StartAsync();

    public Task DisposeAsync() => _server.DisposeAsync().AsTask().WaitAsync(CleanupBound);

    private static async IAsyncEnumerable<ManagerEvent> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private event Action<MeetMeRoom, MeetMeUser>? Reference;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Remove_ShouldMatchDelegateRemove_WhenAFormerEventIsDrivenLikeAFieldLikeEvent(
        FormerEvent formerEvent, string operations, string expected)
    {
        var heard = new List<string>();
        var handlers = new Dictionary<char, Action<MeetMeRoom, MeetMeUser>>
        {
            ['A'] = Recorder(heard, "A"),
            ['B'] = Recorder(heard, "B"),
            ['C'] = Recorder(heard, "C"),
        };

        foreach (var operation in operations.Split(' '))
        {
            Action<MeetMeRoom, MeetMeUser>? value = null;
            foreach (var name in operation[1..].Split('+'))
                value += handlers[name[0]];

            if (operation[0] == '+')
            {
                Reference += value;
                Subscribe(formerEvent, value!);
            }
            else
            {
                Reference -= value;
                Unsubscribe(formerEvent, value!);
            }
        }

        Reference?.Invoke(new MeetMeRoom { RoomNumber = Room }, new MeetMeUser { Channel = Channel });
        var byReference = heard.ToList();
        heard.Clear();

        Raise(formerEvent);

        heard.Should().Equal(byReference, "the former event removes as the field-like event of 2.10.x did");
        string.Join(' ', byReference).Should().Be(expected);
    }

    [Fact]
    public void Remove_ShouldJudgeTheRunOnTheFormerNameSubscriptions_WhenAConferenceNameHandlerSitsBetween()
    {
        var heard = new List<string>();
        var a = Recorder(heard, "A");
        var b = Recorder(heard, "B");
        _server.MeetMe.UserJoined += a;
        _server.Conferences.UserJoined += (_, _) => heard.Add("X");
        _server.MeetMe.UserJoined += b;

        _server.MeetMe.UserJoined -= a + b;
        Raise(FormerEvent.UserJoined);

        heard.Should().Equal("X");
    }

    private static Action<MeetMeRoom, MeetMeUser> Recorder(List<string> heard, string name) => (_, _) => heard.Add(name);

    private void Subscribe(FormerEvent formerEvent, Action<MeetMeRoom, MeetMeUser> value)
    {
        switch (formerEvent)
        {
            case FormerEvent.UserJoined:
                _server.MeetMe.UserJoined += value;
                break;
            case FormerEvent.UserLeft:
                _server.MeetMe.UserLeft += value;
                break;
            default:
                _server.MeetMe.UserStateChanged += value;
                break;
        }
    }

    private void Unsubscribe(FormerEvent formerEvent, Action<MeetMeRoom, MeetMeUser> value)
    {
        switch (formerEvent)
        {
            case FormerEvent.UserJoined:
                _server.MeetMe.UserJoined -= value;
                break;
            case FormerEvent.UserLeft:
                _server.MeetMe.UserLeft -= value;
                break;
            default:
                _server.MeetMe.UserStateChanged -= value;
                break;
        }
    }

    // Raises the event once: a join, then the leave or the mute the event under test reports.
    private void Raise(FormerEvent formerEvent)
    {
        _observer!.OnNext(new ConfbridgeJoinEvent { Conference = Room, Channel = Channel, UniqueId = Channel + "-id" });
        if (formerEvent == FormerEvent.UserLeft)
            _observer.OnNext(new ConfbridgeLeaveEvent { Conference = Room, Channel = Channel, UniqueId = Channel + "-id" });
        else if (formerEvent == FormerEvent.UserStateChanged)
            _observer.OnNext(new ConfbridgeMuteEvent { Conference = Room, Channel = Channel });
    }
}
