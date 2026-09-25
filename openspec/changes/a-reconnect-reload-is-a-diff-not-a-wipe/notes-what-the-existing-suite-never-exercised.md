# Notes — what the existing suite never exercised

Task 1.2 of this change, recorded 2026-09-24 against `fix/reconnect-reload-is-a-diff` at `635f8a6a`
("the failing regression tests for the reconnect reload").

Every finding below is a **textual observation of the tree**, followed by the command that produced
it. None of them was measured at runtime; §4 says what was run, what that leaves unproven, and why no
probe was added. Do not read a verdict in §5 as a test result — no functional test was executed here.

## 1. `Reconnected` is subscribed inside `StartAsync`, and nowhere else

`src/Verbara.Sdk.Live/Server/VerbaraServer.cs`, lines 88-91:

```csharp
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _subscription = _connection.Subscribe(new EventObserver(this));
        _connection.Reconnected += OnReconnected;
```

The subscription set is closed — one `+=` and one `-=`, both in that file, and the handler is
private, so nothing outside the class can attach it:

```console
$ grep -rn 'Reconnected *+=\|Reconnected *-=' src --include='*.cs'
src/Verbara.Sdk.Live/Server/VerbaraServer.cs:91:        _connection.Reconnected += OnReconnected;
src/Verbara.Sdk.Live/Server/VerbaraServer.cs:264:        _connection.Reconnected -= OnReconnected;

$ grep -rn 'void OnReconnected' src --include='*.cs'
src/Verbara.Sdk.Ami/Connection/AmiConnection.cs:592:    private void OnReconnected()
src/Verbara.Sdk.Live/Server/VerbaraServer.cs:122:    private async void OnReconnected()
```

A `VerbaraServer` that was constructed but never started therefore has no handler attached for
`Reconnected`: the reload path (`Channels.Clear()` … `RequestInitialStateAsync`) cannot run on it.

## 2. The shared `SessionTestFixture` never starts the server

`Tests/Verbara.Sdk.Sessions.FunctionalTests/Infrastructure/SessionTestFixture.cs` constructs the
server and attaches the session manager, and its `IAsyncLifetime` hook does nothing:

```csharp
        Server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        SessionManager = new CallSessionManager(…);

        SessionManager.AttachToServer(Server, DefaultServerId);
```

```csharp
    public Task InitializeAsync() => Task.CompletedTask;
```

As committed, the only `StartAsync` call in the whole project belongs to task 1.1's new tests:

```console
$ git grep -n 'StartAsync' 635f8a6a -- Tests/Verbara.Sdk.Sessions.FunctionalTests
635f8a6a:…/ReconnectReloadTests.cs:89:    /// constructed never hears a reconnect at all. <c>StartAsync</c> ends with its own
635f8a6a:…/ReconnectReloadTests.cs:94:        await _server.StartAsync();
```

(Task 1.5's `InitialLoadTests.cs` was still untracked in the working tree while this was written and
starts a server too — also with its own harness, not the fixture. The fixture itself is untouched.)

So any future test that raises `Reconnected` on `_fixture.Server` will observe nothing happening.
That is the trap; `ReconnectReloadTests.GivenAStartedServer()` exists to avoid it.

## 3. Stronger than the brief: the reconnect test never raises `Reconnected` at all

`ReconciliationTests.Reconnection_ShouldCleanSessions_WhenServerReconnects`
(`Tests/Verbara.Sdk.Sessions.FunctionalTests/ReconciliationTests.cs`, lines 132-157 at `635f8a6a`;
11 lines lower now that task 1.2's pointer comment sits above it) substitutes a detach/re-attach of
the session manager for a reconnect:

```csharp
        // Detach simulates what happens on reconnect — manager detaches from server
        _fixture.SessionManager.DetachFromServer("test-srv");
        …
        _fixture.SessionManager.GetByLinkedId("rc-linked-5").Should().NotBeNull();
        …
        _fixture.SessionManager.AttachToServer(_fixture.Server, "test-srv");
```

The word "Reconnect" appears in that file only in the test's own name, and the project's only
`Raise.Event` is in the new file:

```console
$ git show 635f8a6a:Tests/Verbara.Sdk.Sessions.FunctionalTests/ReconciliationTests.cs | grep -n 'Reconnect'
133:    public void Reconnection_ShouldCleanSessions_WhenServerReconnects()

$ git grep -n 'Raise.Event' 635f8a6a -- Tests/Verbara.Sdk.Sessions.FunctionalTests
635f8a6a:…/ReconnectReloadTests.cs:119:        _connection.Reconnected += Raise.Event<Action>();
```

(Both are quoted against `635f8a6a` on purpose: task 1.2 then added a pointer comment above that
test, so a fresh `grep 'Reconnect'` over the working tree returns the comment's own lines too.)

The name is wrong twice over. No reconnect is exercised (nothing raises the event, and the fixture's
server has no handler for it anyway), and nothing is cleaned either: line 147 at `635f8a6a` asserts
that the session **survives** the simulated reconnect, which is the very stranded session this change
removes. Whichever change fixes that test owns it; this one only records it.

## 4. What was run, and what is still not measured

Three commands were run, and all three verify task 1.2's own edit rather than any finding above:

```console
$ openspec validate --all --strict          # Totals: 17 passed, 0 failed (17 items); exit 0
$ dotnet build Tests/Verbara.Sdk.Sessions.FunctionalTests/…csproj -v q --nologo
                                            # Build succeeded. 0 Warning(s) 0 Error(s); exit 0
                                            # (output dll rewritten 22:20:02 against a 22:19:12
                                            #  source edit — the compile really ran)
$ dotnet test  Tests/Verbara.Sdk.Sessions.FunctionalTests/…csproj --filter "FullyQualifiedName~ReconciliationTests"
                                            # Passed! Failed: 0, Passed: 7, Skipped: 0, Total: 7
```

No runtime probe was written for §1-§3, and none of the functional tests in §5 was executed:

- A probe would have had to live in a test project that a sibling agent was building at that moment
  (23 MSBuild nodes were up, and task 1.5's `InitialLoadTests.cs` was mid-flight in the same
  project), so it would have risked that agent's measurement for a fact §1 already closes
  textually. Task 1.1's two regression tests are the runtime evidence for the defect itself; this
  note is the source-level map around them.
- The three files in §5 are `[Trait("Category", "Functional")]`, so the default unit filter skips
  them, and the CI lane that runs them is gated (`.github/workflows/ci.yml:378` and `:407`):
  `(github.event_name == 'merge_group' || contains(github.event.pull_request.labels.*.name, 'ci:functional'))`.
  They need Docker, Toxiproxy and a real Asterisk. **"They are green today" is therefore an
  unverified premise in this note** — what is verified is what they assert, not that they pass.

What remains unproven at runtime: that raising `Reconnected` on a constructed-but-unstarted server
does nothing observable. §1 closes it textually (single private subscription site, inside
`StartAsync`); a test would close it by measurement, and Phase B will drive that path anyway.

## 5. The three reconnection files — one verdict each

The brief's paths were off by a directory; the files are:

| File (as found) | Drives `VerbaraServer.OnReconnected`? | Inspects sessions? | Verdict |
|---|---|---|---|
| `Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/Diagnostics/AmiGaugeReconnectTests.cs` | No — never constructs a `VerbaraServer` | No | Stops short: real reconnects (3 container restarts), but the subject is observable-gauge re-registration on `AmiMetrics.Meter`. |
| `Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/Reconnection/AmiReconnectionTests.cs` | No — never constructs a `VerbaraServer` | No | Stops short: the subject is the connection — `State`, a `PingAction` after reconnect, max attempts, backoff log lines. |
| `Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration/Reconnection/LiveStateRecoveryTests.cs` | **Yes** — two tests call `server.StartAsync()` (`:35`, `:82`) | No | Closest, and stops one layer short: it asserts the **channel table**, never a session. |

Counted rather than eyeballed:

```console
$ cd Tests/Verbara.Sdk.FunctionalTests/Layer5_Integration
$ for f in Diagnostics/AmiGaugeReconnectTests.cs Reconnection/AmiReconnectionTests.cs Reconnection/LiveStateRecoveryTests.cs; do
    printf '%-34s VerbaraServer=%s StartAsync=%s session=%s Channels.=%s CallEnded=%s\n' "$(basename $f)" \
      "$(grep -c 'VerbaraServer' $f || true)" "$(grep -c 'StartAsync' $f || true)" \
      "$(grep -ci 'session' $f || true)" "$(grep -c 'Channels\.' $f || true)" "$(grep -c 'CallEnded' $f || true)"
  done
AmiGaugeReconnectTests.cs          VerbaraServer=0 StartAsync=0 session=0 Channels.=0 CallEnded=0
AmiReconnectionTests.cs            VerbaraServer=0 StartAsync=0 session=0 Channels.=0 CallEnded=0
LiveStateRecoveryTests.cs          VerbaraServer=3 StartAsync=2 session=0 Channels.=4 CallEnded=0
```

Why none of them could have been red for this defect:

- **`AmiGaugeReconnectTests`, `AmiReconnectionTests`** — the defect lives in
  `VerbaraServer.OnReconnected`, and neither file instantiates that class. They could not see it.
- **`LiveStateRecoveryTests.VerbaraServer_ShouldReloadState_AfterReconnect`** — asserts only that a
  log line containing "Reconnected" or "reloading state" exists (`:52-57`). It proves the handler
  ran, nothing about its effect.
- **`LiveStateRecoveryTests.ChannelManager_ShouldClearOnReconnect`** — the one test that drives the
  exact scenario of this change's first regression test (channel held, AMI link cut with a
  `reset_peer` toxic so no `Hangup` arrives, Asterisk restarted, reconnect), and it asserts the
  outcome the change *keeps*:

  ```csharp
      // The restarted Asterisk has no channels, so the reload adds none: only the clear on
      // reconnect can remove the channels tracked before the cut.
      server.Channels.ChannelCount.Should().Be(0,
          "the reconnect must clear channels whose Hangup events never arrived");
  ```

  It never attaches a `CallSessionManager`, so the ghost session — the actual defect — is outside
  what it can observe. It also states the wipe as the mechanism ("only the clear on reconnect can
  remove the channels"), which is precisely the sentence this change falsifies: after the fix the
  channels are removed by the diff, and the removal is *announced*.

  Its assertion stays true after the fix (an empty snapshot still empties the table), so **it is not
  a red test waiting to happen — it is a green test whose stated reason becomes wrong**. Its name,
  `ChannelManager_ShouldClearOnReconnect`, names the banned mechanism rather than the guarantee.

## 6. Also found: the unit suite for `Verbara.Sdk.Live` has no reconnect test at all

`Tests/Verbara.Sdk.Live.Tests` owns the unit coverage of `VerbaraServer`. Its five server test files
call `StartAsync` 62 times and mention reconnection zero times:

```console
$ for f in Tests/Verbara.Sdk.Live.Tests/Server/*.cs; do echo -n "$(basename $f): StartAsync="; grep -c 'StartAsync' "$f" || true; done
VerbaraServerCoverageTests.cs: StartAsync=17
VerbaraServerEventRoutingTests.cs: StartAsync=2
VerbaraServerExtendedTests.cs: StartAsync=25
VerbaraServerPoolTests.cs: StartAsync=0
VerbaraServerTests.cs: StartAsync=18

$ grep -rc 'Reconnect' Tests/Verbara.Sdk.Live.Tests/Server/*.cs
Tests/Verbara.Sdk.Live.Tests/Server/VerbaraServerExtendedTests.cs:0
Tests/Verbara.Sdk.Live.Tests/Server/VerbaraServerCoverageTests.cs:0
Tests/Verbara.Sdk.Live.Tests/Server/VerbaraServerTests.cs:0
Tests/Verbara.Sdk.Live.Tests/Server/VerbaraServerEventRoutingTests.cs:0
Tests/Verbara.Sdk.Live.Tests/Server/VerbaraServerPoolTests.cs:0
```

Repo-wide, exactly one pre-existing file both constructs a `VerbaraServer` and lets a reconnect
happen: `LiveStateRecoveryTests`. Every other file that mentions both sets `opts.AutoReconnect = false`
(Queues, Bridge, Concurrency, EventOrdering — including `SessionEventOrderTests`, the only functional
file that does hold a `CallSessionManager`, at `:43`, `:118`, `:194`). So **no test in this repo has
ever driven a reconnect and a session at the same time.** That is the hole, stated as a count.

## 7. What the next person should do with this

1. Whoever fixes `Reconnection_ShouldCleanSessions_WhenServerReconnects` should raise the event, not
   detach the manager — and must call `Server.StartAsync()` first, or fix `SessionTestFixture` to.
   The fixture is instantiated by six test classes (`DomainEventTests`, `IndexAndQueryTests`,
   `LinkedIdCorrelationTests`, `ReconciliationTests`, `SessionLifecycleSmokeTests`,
   `SessionLifecycleTests`), so starting the server there changes what every one of them boots;
   `ReconnectReloadTests` chose its own harness for that reason.
2. Phase B should not change `ChannelManager_ShouldClearOnReconnect`'s assertion, only the comment
   and name that justify it — and that file is inside `Tests/Verbara.Sdk.FunctionalTests`, which
   task 3.3 must coordinate with `a-published-surface-is-one-something-measures`.
3. The session-side scenario `LiveStateRecoveryTests` is missing (a session held across a cut link,
   ended by the reload) is what task 3.1 binds from `specs/live-state-reload/spec.md`.
