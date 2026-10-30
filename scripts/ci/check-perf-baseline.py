#!/usr/bin/env python3
"""Compare a BenchmarkDotNet run against Tests/Verbara.Sdk.Benchmarks/baseline.json.

Usage:
  check-perf-baseline.py --baseline <baseline.json> --artifacts-root <dir>
                         [--enforce] [--markdown-out <file>]

WHAT THIS IS. The ENFORCING guard behind README.md's AMI-throughput row and the rest of the
Performance table's hot paths (ADR-0042 D1/D4). It is a RELATIVE-regression gate: each benchmark's
observed mean is compared against a committed hosted-runner mean within a per-benchmark tolerance
band. It does NOT compare against the README's workstation figures — those are 1.97x-2.35x faster
and gating on them would red this job permanently (ADR-0042 D4, Option D). Document-vs-record
correspondence is COHERENCE's job (D7), enforced per-PR elsewhere; this is regression only.

WHY IT MUST FAIL CLOSED. Every benchmark step in .github/workflows/perf-regression.yml is suffixed
`|| true`, so a benchmark that never ran — bad filter, build break, timeout, renamed class —
produces no report and the job stays green. That is the failure mode this closes. A missing
directory, a missing report, an empty `Benchmarks` array, a baseline-listed benchmark absent from
the parsed set, or a `Statistics.Mean` that is missing or not a positive number is a BREACH, not a
skip. "We did not measure it" never reads as "it is fine".

    NOTE ON UNITS: BenchmarkDotNet's `Statistics.Mean` is in NANOSECONDS, always, and the report
    carries no unit field. Do not infer units from anything; do not read `Statistics.Scaled*`.

    NOTE ON BANDS: never derive a band from `Statistics.StandardDeviation`. That is the WITHIN-run
    deviation (CV 0.11%-0.67% on this suite) and is ~20x tighter than the ACROSS-run spread
    (CV 0.5%-11.2%) that a weekly gate actually experiences, because ubuntu-latest alternates
    between two CPU models. Bands are calibrated from observed runs and committed by hand
    (ADR-0042 D6); see baseline.README.md.

BYTES DECIDE, TIME INFORMS. A baseline entry may also carry `bytes` (BenchmarkDotNet's
`Memory.BytesAllocatedPerOperation`) and `bytes_tolerance` (an absolute two-sided bound in bytes,
default 0 = exact). Allocated bytes do not depend on the CPU, its vector width or the runner fleet:
measured identical with the JIT targeting x86-64-v3 and x86-64-v4 on every synchronous row of this
suite, where the same pair moved time by at most about 5% on that host (the spread across hosted
runners is measured by a re-calibration under the ISA pin). So a row whose allocation is deterministic is
gated on its bytes, exactly or within a stated bound, and a row may be baselined on bytes alone
(no `mean_ns`/`tolerance_pct`) until its hosted-runner time spread has been observed. A baselined
row with bytes but no `Memory.BytesAllocatedPerOperation` in its report is a structural failure:
the memory diagnoser was removed, and the gate would otherwise stop measuring bytes in silence.

PINNED ISA. The JIT targets the widest instruction set the host offers, and the hosted fleet mixes
CPUs with and without AVX-512, so the same code produced time "breaches" in both directions on runs
where nothing in this repository changed. The workflow pins the JIT to x86-64-v3 with
`DOTNET_EnableAVX512=0` (on .NET 10 the older `DOTNET_EnableAVX512F=0` is accepted and ignored).
When the baseline declares `"jit_isa"`, every declared source's BenchmarkDotNet log must name that
ISA and no other ("RyuJIT x86-64-v3"); a log that names another, or no log at all, is a structural
failure, so the pin cannot fall off without the gate saying so.

OBSERVING-ONLY BY DEFAULT (openspec `enforce-unguarded-public-claims` §2.5). A gate whose first act
is a false red gets routed around and then removed. So the comparison lands reporting-but-not-
failing, and flips to failing in a LATER PR once two consecutive scheduled runs have passed under
observation. THE FLIP IS ONE LINE: `PERF_GATE_ENFORCE: 'false'` -> `'true'` in
.github/workflows/perf-regression.yml (or pass --enforce). Nothing else changes; the comparison,
the annotations and the breach issue all already run.

Exit codes:
  0  pass, or observing-mode with findings (findings are reported, the job is not failed)
  1  enforcing-mode with at least one breach (band or structural)
  2  the gate itself is misconfigured — bad usage, unreadable/invalid baseline. ALWAYS fatal,
     in both modes: this is the guard being broken rather than the guard reporting, and a
     silently-misinvoked guard is the `|| true` hole wearing a different hat.

Stdlib only, no pip deps — same constraint as the coverage guards (verbara-meta/ADR-0013).
Unit-tested by scripts/tests/test_check_perf_baseline.py in the always-on, required
`Coverage Script Tests` job, because a guard that fires only when nobody is looking must have its
own guard.
"""
import json
import os
import re
import sys

REPORT_SUFFIX = "-report-full-compressed.json"


def die(message):
    """Gate-is-broken exit (2). Fatal in both modes — see the module docstring."""
    print(f"::error::perf-baseline: {message}")
    sys.exit(2)


def _is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def parse_args(argv):
    baseline = artifacts_root = markdown_out = None
    enforce = os.environ.get("PERF_GATE_ENFORCE", "").strip().lower() in ("1", "true", "yes", "on")
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == "--enforce":
            enforce = True
        elif arg == "--baseline" and i + 1 < len(argv):
            i += 1
            baseline = argv[i]
        elif arg == "--artifacts-root" and i + 1 < len(argv):
            i += 1
            artifacts_root = argv[i]
        elif arg == "--markdown-out" and i + 1 < len(argv):
            i += 1
            markdown_out = argv[i]
        else:
            die(f"unrecognized or incomplete argument '{arg}'. usage: check-perf-baseline.py "
                f"--baseline <baseline.json> --artifacts-root <dir> [--enforce] "
                f"[--markdown-out <file>]")
        i += 1
    if not baseline or not artifacts_root:
        die("usage: check-perf-baseline.py --baseline <baseline.json> --artifacts-root <dir> "
            "[--enforce] [--markdown-out <file>]")
    return baseline, artifacts_root, markdown_out, enforce


def load_baseline(path):
    """Read + validate the committed baseline. Any problem here is exit 2, not a breach: the
    comparison cannot be performed at all, so calling it 'pass' or 'breach' would both be lies."""
    try:
        with open(path, encoding="utf-8") as handle:
            doc = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        die(f"cannot read baseline '{path}': {exc}")

    if not isinstance(doc, dict):
        die(f"baseline '{path}' is not a JSON object.")

    sources = doc.get("sources")
    if not isinstance(sources, dict):
        die(f"baseline '{path}' has no \"sources\" object mapping artifacts subdirectory -> "
            f"BenchmarkDotNet type.")
    sources = {k: v for k, v in sources.items() if not k.startswith("_")}
    if not sources:
        die(f"baseline '{path}' \"sources\" is empty — nothing would be checked, which is "
            f"indistinguishable from a pass.")
    for subdir, type_name in sources.items():
        if not isinstance(type_name, str) or not type_name:
            die(f"baseline \"sources\" entry '{subdir}' must map to a BenchmarkDotNet type name.")

    benchmarks = doc.get("benchmarks")
    if not isinstance(benchmarks, dict) or not benchmarks:
        die(f"baseline '{path}' has no non-empty \"benchmarks\" object keyed by BenchmarkDotNet "
            f"FullName.")

    parsed = {}
    for full_name, entry in benchmarks.items():
        if full_name.startswith("_"):
            continue
        if not isinstance(entry, dict):
            die(f"baseline entry '{full_name}' is not an object.")
        has_time = "mean_ns" in entry or "tolerance_pct" in entry
        has_bytes = "bytes" in entry or "bytes_tolerance" in entry
        if not has_time and not has_bytes:
            die(f"baseline entry '{full_name}' gates nothing: it needs \"mean_ns\" + "
                f"\"tolerance_pct\", \"bytes\", or both.")
        mean = band = allocated = None
        bound = 0.0
        if has_time:
            mean = entry.get("mean_ns")
            band = entry.get("tolerance_pct")
            if not _is_number(mean) or mean <= 0:
                die(f"baseline entry '{full_name}' has no positive numeric \"mean_ns\".")
            if not _is_number(band) or band <= 0:
                die(f"baseline entry '{full_name}' has no positive numeric \"tolerance_pct\".")
            mean, band = float(mean), float(band)
        if has_bytes:
            allocated = entry.get("bytes")
            if not _is_number(allocated) or allocated < 0:
                die(f"baseline entry '{full_name}' has no non-negative numeric \"bytes\".")
            bound = entry.get("bytes_tolerance", 0)
            if not _is_number(bound) or bound < 0:
                die(f"baseline entry '{full_name}' \"bytes_tolerance\" must be a non-negative "
                    f"number of bytes.")
            allocated, bound = float(allocated), float(bound)
        parsed[full_name] = {"mean_ns": mean, "band_pct": band,
                             "bytes": allocated, "bytes_tolerance": bound}
    if not parsed:
        die(f"baseline '{path}' \"benchmarks\" has no entries.")

    jit_isa = doc.get("jit_isa")
    if jit_isa is not None and (not isinstance(jit_isa, str) or not jit_isa):
        die(f"baseline '{path}' \"jit_isa\" must be a non-empty string such as \"x86-64-v3\".")

    # Every baselined benchmark must belong to a declared source, or its report would never be
    # looked for and its absence would go unnoticed — a hole the same shape as `|| true`.
    types = set(sources.values())
    for full_name in parsed:
        owner = full_name.rsplit(".", 1)[0]
        if owner not in types:
            die(f"baseline entry '{full_name}' belongs to type '{owner}', which no \"sources\" "
                f"row declares. Add the workflow filter step and its sources row, or remove "
                f"the entry.")
    return sources, parsed, jit_isa


ISA_PATTERN = re.compile(r"RyuJIT (x86-64-v[0-9]+)")


def check_isa(artifacts_root, sources, jit_isa):
    """Structural failures for every declared source whose BenchmarkDotNet log does not show the
    pinned JIT ISA. Sources with no results directory are skipped: collect_observed already reports
    them, once."""
    structural = []
    if not jit_isa:
        return structural
    for subdir in sorted(sources):
        directory = os.path.join(artifacts_root, subdir)
        if not os.path.isdir(os.path.join(directory, "results")):
            continue
        seen = set()
        logs = sorted(name for name in os.listdir(directory) if name.endswith(".log"))
        for name in logs:
            try:
                with open(os.path.join(directory, name), encoding="utf-8", errors="replace") as handle:
                    seen.update(ISA_PATTERN.findall(handle.read()))
            except OSError:
                continue
        if not seen:
            structural.append(
                f"[{sources[subdir]}] no BenchmarkDotNet log in '{directory}' names the JIT ISA, so "
                f"the pin to {jit_isa} cannot be confirmed.")
        elif seen != {jit_isa}:
            structural.append(
                f"[{sources[subdir]}] the JIT targeted {', '.join(sorted(seen))}, not the pinned "
                f"{jit_isa}: the ISA pin did not take, and its times are not comparable to the "
                f"baseline.")
    return structural


def collect_observed(artifacts_root, sources):
    """Walk the declared sources and return (observed FullName -> mean_ns, structural failures).

    Every one of the five fail-closed conditions in ADR-0042 D4 is a structural failure recorded
    here rather than an exception, so ONE run reports ALL of them instead of stopping at the first.
    """
    observed = {}
    structural = []
    for subdir in sorted(sources):
        type_name = sources[subdir]
        results_dir = os.path.join(artifacts_root, subdir, "results")
        report = os.path.join(results_dir, f"{type_name}{REPORT_SUFFIX}")

        if not os.path.isdir(results_dir):
            structural.append(
                f"[{type_name}] results directory '{results_dir}' does not exist — the benchmark "
                f"never produced output. Its workflow step is suffixed `|| true`, so this is the "
                f"case that would otherwise read green.")
            continue
        if not os.path.isfile(report):
            present = sorted(os.listdir(results_dir)) or ["<empty>"]
            structural.append(
                f"[{type_name}] report '{report}' is missing. Present in that directory: "
                f"{', '.join(present)}. A renamed/moved benchmark class needs the baseline "
                f"\"sources\" row updated in the same PR.")
            continue
        try:
            with open(report, encoding="utf-8") as handle:
                doc = json.load(handle)
        except (OSError, json.JSONDecodeError) as exc:
            structural.append(f"[{type_name}] report '{report}' is unreadable/unparseable: {exc}")
            continue
        if not isinstance(doc, dict):
            structural.append(f"[{type_name}] report '{report}' is not a JSON object.")
            continue

        entries = doc.get("Benchmarks")
        if not isinstance(entries, list) or not entries:
            structural.append(
                f"[{type_name}] report '{report}' has an empty or missing \"Benchmarks\" array — "
                f"the run produced a file but measured nothing.")
            continue

        for index, entry in enumerate(entries):
            if not isinstance(entry, dict) or not isinstance(entry.get("FullName"), str) \
                    or not entry["FullName"]:
                structural.append(
                    f"[{type_name}] report '{report}' entry #{index} has no \"FullName\" — "
                    f"malformed report, refusing to read it as green.")
                continue
            observed[entry["FullName"]] = (entry, report)
    return observed, structural


def _observed_bytes(entry):
    memory = entry.get("Memory") if isinstance(entry, dict) else None
    value = memory.get("BytesAllocatedPerOperation") if isinstance(memory, dict) else None
    return float(value) if _is_number(value) and value >= 0 else None


def _observed_mean(entry):
    stats = entry.get("Statistics") if isinstance(entry, dict) else None
    value = stats.get("Mean") if isinstance(stats, dict) else None
    return float(value) if _is_number(value) and value > 0 else None


def evaluate(baseline, observed):
    """Return (rows, breaches, structural) for the baselined set. Two-sided by design: the SLOWER
    side is the regression this exists to catch; the FASTER side is a stale baseline (ADR-0042 D6)
    or a benchmark whose work got optimised away, and both want a human. Bytes are two-sided for
    the same reasons: MORE is the regression, FEWER is a stale baseline or work that went away."""
    rows = []
    breaches = []
    structural = []
    for full_name in sorted(baseline):
        expected = baseline[full_name]
        mean_ns, band_pct = expected["mean_ns"], expected["band_pct"]
        want_bytes, bytes_bound = expected["bytes"], expected["bytes_tolerance"]
        if full_name not in observed:
            wanted = []
            if mean_ns is not None:
                wanted.append(f"{mean_ns:g} ns +/- {band_pct:g}%")
            if want_bytes is not None:
                wanted.append(f"{want_bytes:g} B +/- {bytes_bound:g} B")
            structural.append(
                f"[{full_name}] is in baseline.json but absent from the parsed results. The "
                f"benchmark was removed, renamed, or never ran. Baseline expected "
                f"{' and '.join(wanted)}.")
            continue
        entry, report = observed[full_name]
        stats = entry.get("Statistics")
        if not isinstance(stats, dict) or "Mean" not in stats:
            structural.append(
                f"[{full_name}] has no \"Statistics.Mean\" in '{report}' — the benchmark entry "
                f"exists but carries no measurement.")
            continue
        raw = stats["Mean"]
        if not _is_number(raw) or raw <= 0:
            structural.append(
                f"[{full_name}] \"Statistics.Mean\"={raw!r} in '{report}' is not a positive "
                f"number (BenchmarkDotNet means are nanoseconds).")
            continue
        actual = float(raw)
        actual_bytes = _observed_bytes(entry)
        verdicts = []

        delta_pct = None
        if mean_ns is not None:
            delta_pct = (actual - mean_ns) / mean_ns * 100.0
            over = abs(delta_pct) - band_pct
            if over > 0:
                direction = "SLOWER" if delta_pct > 0 else "FASTER"
                verdicts.append(f"BREACH ({direction})")
                breaches.append(
                    f"[{full_name}] {direction} than baseline by {abs(delta_pct):.2f}%, outside "
                    f"the +/-{band_pct:g}% band by {over:.2f} pp. baseline {mean_ns:g} ns, "
                    f"observed {actual:.2f} ns.")

        bytes_delta = None
        if want_bytes is not None:
            if actual_bytes is None:
                structural.append(
                    f"[{full_name}] is baselined on bytes but '{report}' carries no numeric "
                    f"\"Memory.BytesAllocatedPerOperation\" — the memory diagnoser is off, so "
                    f"the bytes gate measured nothing.")
                continue
            bytes_delta = actual_bytes - want_bytes
            if abs(bytes_delta) > bytes_bound:
                direction = "MORE-BYTES" if bytes_delta > 0 else "FEWER-BYTES"
                verdicts.append(f"BREACH ({direction})")
                breaches.append(
                    f"[{full_name}] allocates {actual_bytes:g} B per operation against a baseline "
                    f"of {want_bytes:g} B ({bytes_delta:+g} B), outside the +/-{bytes_bound:g} B "
                    f"bound. Bytes do not depend on the runner or its ISA: this is the code.")

        rows.append({
            "name": full_name,
            "baseline_ns": mean_ns,
            "observed_ns": actual,
            "delta_pct": delta_pct,
            "band_pct": band_pct,
            "baseline_bytes": want_bytes,
            "observed_bytes": actual_bytes,
            "bytes_delta": bytes_delta,
            "bytes_tolerance": bytes_bound if want_bytes is not None else None,
            "verdict": " ".join(verdicts) or "ok",
        })
    return rows, breaches, structural


def unbaselined(baseline, observed):
    """Benchmarks measured but not baselined. Reported, never fatal: a new benchmark method inside
    an already-gated class is a baseline TODO (see baseline.README.md), not a regression, and
    failing on it would block the very PR that adds the benchmark."""
    return sorted(set(observed) - set(baseline))


def _short(full_name):
    parts = full_name.split(".")
    return ".".join(parts[-2:]) if len(parts) >= 2 else full_name


def _fmt(value, spec, empty="—"):
    return empty if value is None else format(value, spec)


def render_markdown(rows, breaches, structural, extras, enforce):
    mode = "ENFORCING" if enforce else "OBSERVING (reports, does not fail the job — §2.5)"
    out = ["## Perf regression vs `Tests/Verbara.Sdk.Benchmarks/baseline.json`", "",
           f"Mode: **{mode}**", "",
           "Bytes decide, time informs: allocated bytes per operation do not depend on the runner "
           "or its ISA.", ""]
    if structural:
        out += [f"### Structural failures ({len(structural)})", "",
                "A benchmark that never ran is not a pass (ADR-0042 D4).", ""]
        out += [f"- {item}" for item in structural] + [""]
    if breaches:
        out += [f"### Breaches ({len(breaches)})", ""]
        out += [f"- {item}" for item in breaches] + [""]
    if rows:
        out += ["### Measured", "",
                "| benchmark | baseline (ns) | observed (ns) | delta | band | baseline (B) | "
                "observed (B) | delta (B) | bound (B) | |",
                "|---|--:|--:|--:|--:|--:|--:|--:|--:|---|"]
        for row in rows:
            out.append(
                f"| `{_short(row['name'])}` | {_fmt(row['baseline_ns'], 'g')} | "
                f"{row['observed_ns']:.2f} | {_fmt(row['delta_pct'], '+.2f')}"
                f"{'' if row['delta_pct'] is None else '%'} | "
                f"{'—' if row['band_pct'] is None else '±' + format(row['band_pct'], 'g') + '%'} | "
                f"{_fmt(row['baseline_bytes'], 'g')} | {_fmt(row['observed_bytes'], 'g')} | "
                f"{_fmt(row['bytes_delta'], '+g')} | "
                f"{'—' if row['bytes_tolerance'] is None else '±' + format(row['bytes_tolerance'], 'g')} | "
                f"{row['verdict']} |")
        out.append("")
    if extras:
        out += [f"### Measured but not baselined ({len(extras)})", "",
                "Not a failure. Add a `benchmarks` entry once its hosted-runner spread is "
                "observed — see `baseline.README.md`.", "",
                "| benchmark | observed (ns) | observed (B) |", "|---|--:|--:|"]
        out += [f"| `{name}` | {_fmt(mean, '.2f')} | {_fmt(allocated, 'g')} |"
                for name, mean, allocated in extras] + [""]
    if not breaches and not structural:
        out += ["All baselined benchmarks are inside their bands.", ""]
    return "\n".join(out)


def main(argv):
    baseline_path, artifacts_root, markdown_out, enforce = parse_args(argv)
    sources, baseline, jit_isa = load_baseline(baseline_path)

    observed_raw, structural = collect_observed(artifacts_root, sources)
    structural += check_isa(artifacts_root, sources, jit_isa)
    rows, breaches, more_structural = evaluate(baseline, observed_raw)
    structural += more_structural
    extras = [(name, _observed_mean(observed_raw[name][0]), _observed_bytes(observed_raw[name][0]))
              for name in unbaselined(baseline, observed_raw)]

    mode = "ENFORCING" if enforce else "OBSERVING"
    print(f"perf-baseline: mode={mode} baseline={baseline_path} artifacts={artifacts_root}")
    print(f"perf-baseline: jit_isa={jit_isa or '(not pinned)'}")
    print(f"{'benchmark':<62} {'baseline':>12} {'observed':>12} {'delta':>9} {'band':>7} "
          f"{'base B':>10} {'obs B':>10} {'dB':>8}  verdict")
    for row in rows:
        delta = "-" if row["delta_pct"] is None else f"{row['delta_pct']:+.2f}%"
        band = "-" if row["band_pct"] is None else f"{row['band_pct']:.0f}%"
        print(f"{_short(row['name']):<62} {_fmt(row['baseline_ns'], '.2f', '-'):>12} "
              f"{row['observed_ns']:>12.2f} {delta:>9} {band:>7} "
              f"{_fmt(row['baseline_bytes'], 'g', '-'):>10} {_fmt(row['observed_bytes'], 'g', '-'):>10} "
              f"{_fmt(row['bytes_delta'], '+g', '-'):>8}  {row['verdict']}")

    level = "error" if enforce else "warning"
    for item in structural:
        print(f"::{level}::perf-baseline STRUCTURAL: {item}")
    for item in breaches:
        print(f"::{level}::perf-baseline BREACH: {item}")
    for name, _mean, _allocated in extras:
        print(f"::notice::perf-baseline: '{name}' is measured but has no baseline entry.")

    failed = len(breaches) + len(structural)
    status = "pass" if failed == 0 else "breach"

    if markdown_out:
        try:
            with open(markdown_out, "w", encoding="utf-8") as handle:
                handle.write(render_markdown(rows, breaches, structural, extras, enforce))
        except OSError as exc:
            die(f"cannot write markdown report '{markdown_out}': {exc}")

    github_output = os.environ.get("GITHUB_OUTPUT")
    if github_output:
        try:
            with open(github_output, "a", encoding="utf-8") as handle:
                handle.write(f"status={status}\n")
                handle.write(f"breaches={len(breaches)}\n")
                handle.write(f"structural={len(structural)}\n")
                handle.write(f"enforcing={'true' if enforce else 'false'}\n")
        except OSError as exc:
            die(f"cannot write GITHUB_OUTPUT '{github_output}': {exc}")

    if failed == 0:
        print(f"perf-baseline: OK — {len(rows)} benchmark(s) inside their bands and bounds.")
        return 0

    print(f"perf-baseline: {len(breaches)} band breach(es), {len(structural)} structural "
          f"failure(s).")
    if enforce:
        return 1
    # §2.5: observing-only. Reported loudly, issue filed by the caller, job stays green until the
    # one-line flip to PERF_GATE_ENFORCE=true.
    print("perf-baseline: OBSERVING mode — not failing the job. Flip PERF_GATE_ENFORCE to 'true' "
          "in .github/workflows/perf-regression.yml once two consecutive scheduled runs pass.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
