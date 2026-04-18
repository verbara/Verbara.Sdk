# Asterisk.Sdk.Benchmarks

Micro-benchmarks for the SDK's hot paths, driven by [BenchmarkDotNet](https://benchmarkdotnet.org/).

## What's measured

| Benchmark class | Target |
|-----------------|--------|
| `AmiProtocolReaderBenchmark` | AMI event parse (bytes → `ManagerEvent`) |
| `AmiProtocolWriterBenchmark` | AMI action serialize (`AmiAction` → bytes) |
| `ActionCorrelationBenchmark` | Request/response correlation via `ConcurrentDictionary<string, TCS>` |
| `AsyncEventPumpBenchmark` | Channel-based event pump throughput |
| `EventDeserializerBenchmark` | Source-generated `EventDeserializer` lookup |
| `ChannelManagerBenchmark` | `ChannelManager` O(1) lookups (primary + secondary index) |
| `ObserverDispatchBenchmark` | Copy-on-write volatile array observer dispatch |
| `ConcurrentThroughputBenchmark` | AMI end-to-end on a single connection |
| `AriJsonBenchmark` | ARI source-generated JSON deserialization |
| `AriParseEventBenchmark` | ARI WebSocket event parse |
| `AudioSocketBenchmark` | AudioSocket frame framing/unframing |
| `VoiceAiBenchmarks` | STT/TTS `ProviderName` virtual vs `GetType().Name` fallback (v1.10.0+) |

## Running

**All benchmarks** (long — typically 20-40 min on a Ryzen 9 9900X):

```sh
dotnet run -c Release --project Tests/Asterisk.Sdk.Benchmarks/
```

**A single class** (fast feedback loop):

```sh
dotnet run -c Release --project Tests/Asterisk.Sdk.Benchmarks/ -- \
    --filter "*VoiceAi*"
```

**Smoke test only** (1 iteration, no statistical validity — for CI gating only):

```sh
dotnet run -c Release --project Tests/Asterisk.Sdk.Benchmarks/ -- \
    --filter "*ChannelManager*" --iterationCount 1 --warmupCount 1
```

## Reading the output

Every run writes artifacts under `BenchmarkDotNet.Artifacts/results/`:

- `*.md` — Markdown table (commit to `docs/analysis/` when you rerun the official suite)
- `*.csv` — CSV for scripting / charting
- `*.html` — interactive HTML report
- `*.log` — verbose log (attach for regressions)

The column `Ratio` compares each case against the benchmark marked `[Benchmark(Baseline = true)]` — use it to see perf deltas at a glance.

## Official baseline

The canonical numbers published in [docs/analysis/benchmark-analysis.md](../../docs/analysis/benchmark-analysis.md) were captured on:

| Dimension | Value |
|-----------|-------|
| CPU | AMD Ryzen 9 9900X (12C/24T, 4.4 GHz base) |
| RAM | 64 GB DDR5-6000 |
| OS | Debian 13 (bookworm) 6.12.x kernel |
| .NET | 10.0.x (pinned in `global.json`) |
| BenchmarkDotNet | 0.14.0 |
| Governor | `performance` |

Re-runs on different hardware will scale roughly linearly for ALU-heavy paths and drift more for allocation-sensitive paths.

## Updating the published analysis

1. Run the full suite: `dotnet run -c Release --project Tests/Asterisk.Sdk.Benchmarks/`
2. Pick the `*.md` reports you want to cite from `BenchmarkDotNet.Artifacts/results/`.
3. Edit `docs/analysis/benchmark-analysis.md` — preserve the narrative, paste fresh tables, note the date/commit SHA.
4. Commit both the regenerated markdown report(s) and the updated analysis under `docs(bench):`.

The raw `BenchmarkDotNet.Artifacts/` directory is in `.gitignore` — only the curated markdown in `docs/analysis/` is versioned.
