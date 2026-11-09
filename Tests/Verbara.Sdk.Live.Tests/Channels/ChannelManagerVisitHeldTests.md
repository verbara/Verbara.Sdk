# Held-channel visitors

`ChannelManager.VisitHeld` is internal. The unrestricted overload walks the held table; the linked-id overload walks only the immutable group supplied by the linked-id index, including the engine fallback from a null or empty linked id to the unique id.

Each candidate takes its own channel lock, then briefly takes the manager gate to check that the table still maps its unique id to that exact instance. The manager gate is released before invoking the callback. Callers enter outside a call lock; the callback can then take a call lock in channel → call order. A departing instance is skipped even if the linked-id index still retains it for its notification.

The tests acquire a real `CallSession.SyncRoot`, exercise a concurrent operation that takes the manager gate, and hold dedicated visitor threads at an actual channel-lock wait. They also pin a captured old instance replaced under the same unique id, linked-id restriction/fallback, and both hangup and reload removal. Removal may leave the table during a callback, but the channel state mutation and removal post must wait for that callback to release the channel lock; the outbox raises the removal afterwards with the channel lock released. These guarantees apply to explicit visits. Live notification overtaking remains governed by the existing outbox behavior.

Run the focused cases with `dotnet test Tests/Verbara.Sdk.Live.Tests --filter FullyQualifiedName~ChannelManagerVisitHeldTests`.
