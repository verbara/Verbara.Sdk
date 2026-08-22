# voiceai-session-teardown-races

Two cancellation/teardown races in `src/` surfaced by ADR-0045's instrumentation: the AudioSocket hangup/dispose race and the unguarded `session.update` send in the Realtime bridge
