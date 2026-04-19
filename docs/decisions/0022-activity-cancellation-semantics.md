# ADR-0022: Activity cancellation semantics (`CancelAsync()` separate from `CancellationToken`)

- **Status:** Accepted
- **Date:** 2026-04-18 (retrospective — decision made during the v1.11.0 contact-center activities release)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0012 (Live aggregate root orthogonal to AMI/ARI)

## Context

`Asterisk.Sdk.Activities` models in-flight contact-center operations — AttendedTransfer, ChanSpy, Snoop, Barge — as long-running asynchronous state machines. Every activity moves through a small set of terminal statuses: completed, cancelled, failed. Consumers need two things from the API: a way to observe the current state as it evolves, and a way to cancel an in-flight activity.

The idiomatic .NET choice is to pass a `CancellationToken` into the activity's `StartAsync` method; the caller keeps the `CancellationTokenSource` and calls `Cancel()` on it to cancel. It works for simple async methods. It does not work well for activities because of two concrete problems.

First, token cancellation throws `OperationCanceledException` into the awaiting code. That is fine for fire-and-forget callers but awkward for consumers that wire the activity into an observable (`Status` subject) and expect terminal transitions to flow through the same subject, not through exceptions. An observable flow that terminates with an exception requires consumers to handle both completion and exception paths; a terminal-status-in-stream flow gives one consumption pattern for every outcome.

Second, token cancellation does not surface who cancelled. An activity can be cancelled by the caller who started it, by a supervising workflow, by a timeout, or by the Asterisk-side event stream (e.g. the target channel was hung up by the customer while the activity was in flight). A token tells the activity "stop"; it does not tell the observer which of those four scenarios happened. Contact-center UIs need that distinction — "agent cancelled the transfer" looks very different on the screen from "customer hung up during the transfer".

The design chosen in v1.11.0 is to keep `CancellationToken` for what it is good at — cooperative cancellation of the internal await chain — and add `CancelAsync()` as a first-class method on `IActivity` that writes a terminal `Cancelled` status into the `Status` observable. Consumers driving the activity from a UI call `CancelAsync()`; consumers observing the activity see the terminal status flow through the same stream they use for every other status transition.

## Decision

`IActivity` exposes `CancelAsync(CancellationToken ct = default)` as a first-class method that transitions the activity to the terminal `Cancelled` status and completes the `Status` observable. `CancellationToken` remains available on the internal awaits for cooperative cancellation of the underlying async chain, but consumers observe outcomes through `Status`, not through exceptions. Activity implementations map every terminal case — cancelled by caller, cancelled by supervising workflow, customer hung up — onto an appropriate `Status` transition so observers see the same shape of stream regardless of cause.

## Consequences

- **Positive:**
  - Consumers have one consumption pattern for every outcome: subscribe to `Status`, wait for a terminal transition, react. No need to catch `OperationCanceledException` alongside the observable.
  - The terminal status carries structured context (cancellation source, reason) that a token does not convey. UI code can render different messages for "agent cancelled" vs "customer hung up" directly from the `Status` payload.
  - `CancelAsync()` is discoverable via IntelliSense; a token is an input parameter and does not hint that the method exists for consumer use.
  - The four contact-center activities shipping in v1.11.0 all follow the same pattern, giving consumers a consistent API across AttendedTransfer, ChanSpy, Snoop, and Barge.
- **Negative:**
  - Two cancellation mechanisms coexist: `CancellationToken` on `StartAsync`, and `CancelAsync()` on `IActivity`. A consumer might be unsure which to call, and the wrong choice (cancelling the token instead of calling `CancelAsync()`) produces a correct-but-unidiomatic result — the activity stops, but the terminal status says "failed with OperationCanceledException" instead of "cancelled".
  - Documentation has to be clear about the contract: `CancelAsync()` is the user-facing cancellation; the token is for structured async plumbing.
- **Trade-off:** We trade the uniformity of "just use a token" for an API where the observable is the single source of truth for outcomes. The observable-first shape matches how contact-center UIs consume activities; the token-first shape matches library internals. Exposing both lets each side use the mechanism that fits it. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #7 flagged this as a decision whose rationale is invisible from the interface signature alone.

## Alternatives considered

- **Token-only cancellation, leave callers to infer terminal state** — rejected because it forces consumers to bridge between exception-based cancellation and status-based completion in their own code. Every UI-driving consumer would write the same glue; lifting it into the SDK is the correct move.
- **`CancelAsync()` without a `CancellationToken` on `StartAsync`** — rejected because internal implementations still need cooperative cancellation for structured concurrency (linking to the host's `ApplicationStopping`, timing out on network awaits, propagating into inner `HttpClient` / `ClientWebSocket` calls). Removing the token removes that plumbing.
- **`IActivity.Status` emits `OperationCanceledException` as an observable error** — rejected because the `Rx` `OnError` path is terminal for observables in a way that loses downstream transitions the activity might still want to emit (e.g. cleanup confirmation). A terminal status carries more information than an error.
- **Two separate activity interfaces (`ICancellableActivity` / `IActivity`)** — rejected as over-engineering. Every shipping activity is cancellable; there is no meaningful non-cancellable activity the SDK ships today. Splitting the interface just to document a capability all implementations have does not earn its keep.
