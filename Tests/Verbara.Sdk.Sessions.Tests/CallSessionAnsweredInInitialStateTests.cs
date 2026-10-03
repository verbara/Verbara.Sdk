using FluentAssertions;

namespace Verbara.Sdk.Sessions.Tests;

/// <summary>
/// The ending of a call answered while still in its initial state goes around the transition table, so the
/// method that ends it guards its own state: it acts only on a session that is still <c>Created</c> and holds
/// an observed answer, and on any other session it changes nothing.
/// </summary>
public sealed class CallSessionAnsweredInInitialStateTests
{
    private static readonly DateTimeOffset Answer = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static CallSession CreateSession() => new("test-session", "linked-1", "server-1", CallDirection.Inbound);

    private sealed record Fields(
        CallSessionState State, DateTimeOffset? ConnectedAt, DateTimeOffset? CompletedAt, DateTimeOffset? AnsweredAt);

    private static Fields FieldsOf(CallSession session) =>
        new(session.State, session.ConnectedAt, session.CompletedAt, session.AnsweredInInitialStateAt);

    [Fact]
    public void TryCompleteAnsweredInInitialState_ShouldRefuseAndChangeNothing_WhenTheSessionAlreadyFailed()
    {
        var session = CreateSession();
        session.RecordAnswerInInitialState(Answer);
        session.TryTransition(CallSessionState.Failed).Should().BeTrue();
        var before = FieldsOf(session);

        var acted = session.TryCompleteAnsweredInInitialState();

        new { Acted = acted, After = FieldsOf(session) }.Should().BeEquivalentTo(new { Acted = false, After = before },
            "a call that already ended failed keeps its ending, even though an answer was recorded before it ended");
    }

    [Fact]
    public void TryCompleteAnsweredInInitialState_ShouldRefuseAndChangeNothing_WhenTheSessionAlreadyCompleted()
    {
        var session = CreateSession();
        session.RecordAnswerInInitialState(Answer);
        session.State = CallSessionState.Completed;
        session.CompletedAt = Answer.AddMinutes(1);
        var before = FieldsOf(session);

        var acted = session.TryCompleteAnsweredInInitialState();

        new { Acted = acted, After = FieldsOf(session) }.Should().BeEquivalentTo(new { Acted = false, After = before },
            "a call that already ended completed is not ended a second time");
    }

    [Fact]
    public void TryCompleteAnsweredInInitialState_ShouldRefuseAndChangeNothing_WhenNoAnswerWasRecorded()
    {
        var session = CreateSession();
        var before = FieldsOf(session);

        var acted = session.TryCompleteAnsweredInInitialState();

        new { Acted = acted, After = FieldsOf(session) }.Should().BeEquivalentTo(new { Acted = false, After = before },
            "a call in its initial state whose answer was never observed never became a call");
    }

    [Fact]
    public void TryCompleteAnsweredInInitialState_ShouldEndCompletedFromTheObservedAnswer_WhenTheSessionIsInItsInitialStateWithAnAnswer()
    {
        var session = CreateSession();
        session.RecordAnswerInInitialState(Answer);
        session.RecordAnswerInInitialState(Answer.AddSeconds(5));

        var acted = session.TryCompleteAnsweredInInitialState();

        new { Acted = acted, session.State, session.ConnectedAt, CompletedAtIsSet = session.CompletedAt.HasValue }
            .Should().BeEquivalentTo(new { Acted = true, State = CallSessionState.Completed, ConnectedAt = Answer, CompletedAtIsSet = true },
                "the call was answered and is over, so it completed, connected from its first observed answer");
    }
}
