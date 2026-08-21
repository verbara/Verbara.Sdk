# Tasks: voiceai-session-teardown-races

## 1. Reproduce both, deterministically, before fixing either

- [ ] 1.1 Write a failing test for the AudioSocket race that orders the hangup **before** the
      consumer's first `MoveNextAsync` by construction — not by sleeping. Record the exact failure
      text, including where in the enumerator the `ObjectDisposedException` surfaces.
- [ ] 1.2 Write a failing test for the bridge that lands the cancel inside the setup window
      (connect / lock acquisition / `session.update` send) by construction. Assert on all four
      consequences, not just the exception: the escaping `OperationCanceledException`, the absent
      clean close, the missing `SessionsCompleted` increment, and the missing `SessionEnded` log.
- [ ] 1.3 Confirm both tests fail for the stated reason and not for an adjacent one. A race test
      that goes green when the race is absent proves nothing about the fix.

## 2. Decide the AudioSocket termination semantics — before writing the fix

- [ ] 2.1 Choose between "a hangup completes the audio sequence normally" and "a hangup throws
      `ObjectDisposedException` deterministically". The first matches the domain — a hangup is how
      calls end — and spares every consumer a catch; the second is consistent with the type's other
      members. State the reasoning.
- [ ] 2.2 Record the choice as an ADR addendum or a new ADR referenced from this change. It is a
      public behavioural contract for an MIT SDK, so it must not be inferable only from the diff.
- [ ] 2.3 Decide what happens on the *other* orderings too: hangup mid-enumeration, and
      `ReadAudioAsync` called after an explicit `DisposeAsync` by the consumer. The requirement
      should cover all three, or say why it does not.

## 3. Fix

- [ ] 3.1 `AudioSocketSession`: capture the session token at construction so `ReadAudioAsync` no
      longer touches `_cts.Token` after `_cts.Dispose()` may have run.
- [ ] 3.2 Apply the §2.1 semantics, and make the disposed-state behaviour consistent across the
      type's public surface (`ReadAudioAsync` is currently the only member with no
      `ObjectDisposedException.ThrowIf` guard).
- [ ] 3.3 `OpenAiRealtimeBridge`: bring `ConnectAsync`, `wsWriteLock.WaitAsync` and the
      `session.update` `SendAsync` under the same cancellation handling as the loops.
- [ ] 3.4 Ensure the clean-close path and the terminal telemetry (`SessionsCompleted`,
      `SessionDurationMs`, `SessionEnded`) run when a cancel lands during setup.
- [ ] 3.5 Confirm both §1 tests now pass, and that they still fail when the fix is reverted. A
      regression test that passes over the unfixed code is not a regression test.

## 4. Sweep for the same shape elsewhere

- [ ] 4.1 Both defects are instances of a class, not one-offs. Grep the VoiceAi packages for
      `_cts.Token` read from inside an iterator body, and for an `await …(ct)` outside the
      `try` its `OperationCanceledException` handler guards. Record what the sweep finds, including
      "nothing".
- [ ] 4.2 Route anything found in test code to `websocket-fake-class-ab-sweep` rather than fixing it
      here.

## 5. Verification and release

- [ ] 5.1 `dotnet build Verbara.Sdk.slnx` — 0 warnings, 0 errors.
- [ ] 5.2 Unit lane green with the four-exclusion CI filter.
- [ ] 5.3 Both regression tests run 30× green, idle and under CPU saturation. A race test that is
      only reliable on an idle machine has reintroduced the timing dependency it was written against.
- [ ] 5.4 Confirm the `CancellationProvenanceScanner` guard passes on the new tests — the cancelled
      token goes to the subject, the enumeration takes `CancellationToken.None` (ADR-0052 F3).
- [ ] 5.5 CHANGELOG under `Fixed`, stating the behaviour change plainly: what a consumer saw before,
      what it sees now, and that a `catch (ObjectDisposedException)` around `ReadAudioAsync` becomes
      unnecessary.
- [ ] 5.6 **Version bump** in `Directory.Build.props` — this change touches `src/` and alters
      observable behaviour.
- [ ] 5.7 `openspec validate --all --strict` green.
