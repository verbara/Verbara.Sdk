# ADR-0015: AMI string interning pool (FNV-1a, 2048 buckets)

- **Status:** Accepted
- **Date:** 2026-03-16 (retrospective — decision made during the v0.5.0 hardening sprint)
- **Deciders:** Harol A. Reina H.
- **Related:** ADR-0003 (source generators over reflection), ADR-0008 (AMI exponential backoff)

## Context

AMI event parsing is the hot path of the SDK. A contact center running Asterisk at 100K+ concurrent calls sees AMI event volumes in the 100K events/sec range, with every event carrying 5–15 headers. Without intervention, every header is a fresh `string` allocation: ~1–2 million allocations per second purely for event parsing, feeding Gen0 GC pressure that would dominate the SDK's cost envelope at scale.

The [`AmiProtocolReader`](../../src/Asterisk.Sdk.Ami/Internal/AmiProtocolReader.cs) consumes UTF-8 bytes directly from `System.IO.Pipelines` and must turn every `Key: Value\r\n` line into two `string` instances before the source-generated event deserializer can dispatch. Two structural properties of the AMI protocol make this bearable:

- The **set of keys is bounded.** Across Asterisk 18–23 the SDK tracks 941 unique field names (all emitted by the 278 generated event types). New releases add a handful per year.
- The **set of common values is small.** A large fraction of value occurrences collapse to ~35 high-frequency strings: response statuses (`"Success"`, `"Error"`), channel protocols (`"SIP"`, `"PJSIP"`), channel states (`"Up"`, `"Ringing"`, `"Hangup"`), severity levels, and similar enumerations.

Both properties are stable enough to pre-compute at static initialization and look up from a UTF-8 span without ever allocating a `string` for the common case.

## Decision

Ship a static [`AmiStringPool`](../../src/Asterisk.Sdk.Ami/Internal/AmiStringPool.cs) with two lookup structures, both queried directly from `ReadOnlySpan<byte>`:

- **Key pool** — 2048-bucket array indexed by FNV-1a hash of the UTF-8 bytes. Each bucket holds a linear-scan list of `(byte[] Utf8, string Str)` pairs. All 941 known AMI field keys are pre-computed at static init. Average occupancy: 1.2 entries per bucket; maximum: 5.
- **Value pool** — length-indexed array (up to length 24) of linear-scan buckets holding the same `(byte[] Utf8, string Str)` pair shape. 35 high-frequency values are pre-computed. Maximum 10 entries per length bucket.

The reader calls `AmiStringPool.GetKey(utf8Span)` and `AmiStringPool.GetValue(utf8Span, length)` on every header. On a hit, the pool returns the already-allocated `string` reference; no `Encoding.UTF8.GetString(span)` call runs. On a miss (rare, e.g. a new vendor-specific key or a unique caller ID), the reader falls back to a regular UTF-8 decode.

## Consequences

- **Positive:**
  - ~8–12% heap pressure reduction at sustained load, 20%+ throughput uplift when Gen0 GC is the bottleneck.
  - Zero allocation on the hot path for 99%+ of headers on a steady-state Asterisk event stream.
  - Zero locking: lookup is read-only against frozen static arrays, so the pool scales linearly across cores.
  - Cache-line-friendly buckets (~1.2 entries average, 5 max per key bucket).
  - Complements ADR-0003 and ADR-0008: the source-generated deserializer removes reflection, the pool removes the allocations that would otherwise dominate the cost the generator saved.
- **Negative:**
  - The pool is ~344 LOC of specialized data-structure code that looks like "premature optimization" to a reader unaware of the target workload.
  - New AMI keys from future Asterisk releases require regenerating the pool's pre-computed set; the generator that drives this is itself a maintenance surface.
  - Missed keys silently fall back to allocation — a flood of unknown keys on a misconfigured PBX would erase the pool's benefit without any build-time signal.
- **Trade-off:** We trade code simplicity for measured performance at our target scale. A smaller SDK that used `ConcurrentDictionary<string, string>` or no pooling at all would be easier to reason about, but it would lose the SDK's distinctive benchmark position (1.53M → 1.62M events/s single-thread after commit `41fff67`) and would reintroduce Gen0 pressure that dominates at high density. The audit in [`docs/research/2026-04-19-product-alignment-audit.md`](../research/2026-04-19-product-alignment-audit.md) §4 item #1 flagged this pool as a top-tier load-bearing element that must survive future refactors.

## Alternatives considered

- **No pooling, rely on JIT string interning** — rejected because JIT interning only covers compile-time-known literals; heap-allocated strings emitted by `System.IO.Pipelines` span decoding do not get interned and would pay full allocation cost per event.
- **Generic `ConcurrentDictionary<string, string>`** — rejected because it requires a `string` allocation before the lookup (to hash the key being looked up), introduces per-lookup CAS overhead on the hot path, and is not cache-line friendly at the scale we operate.
- **Source-generated `FrozenDictionary<string, string>`** — considered but rejected for this use case because the hot path lookup is from a UTF-8 span, not a string. Every `FrozenDictionary<string, string>` lookup would still require materializing a string first, defeating the point. `FrozenDictionary<byte[], string>` with a custom comparer was prototyped and lost to the hand-rolled FNV-1a bucket array on both latency and GC.
- **Runtime interning via `string.Intern`** — rejected because `string.Intern` only covers the whole-process intern table, requires a `string` allocation to probe it, and does not support UTF-8-span lookup.
- **Per-connection LRU cache** — rejected because the working set is already small enough to fit entirely in the pre-computed pool, and LRU bookkeeping introduces exactly the locking the current design avoids.
