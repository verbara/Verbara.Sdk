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
        if not isinstance(entry, dict):
            die(f"baseline entry '{full_name}' is not an object.")
        mean = entry.get("mean_ns")
        band = entry.get("tolerance_pct")
        if not _is_number(mean) or mean <= 0:
            die(f"baseline entry '{full_name}' has no positive numeric \"mean_ns\".")
        if not _is_number(band) or band <= 0:
            die(f"baseline entry '{full_name}' has no positive numeric \"tolerance_pct\".")
        parsed[full_name] = (float(mean), float(band))

    # Every baselined benchmark must belong to a declared source, or its report would never be
    # looked for and its absence would go unnoticed — a hole the same shape as `|| true`.
    types = set(sources.values())
    for full_name in parsed:
        owner = full_name.rsplit(".", 1)[0]
        if owner not in types:
            die(f"baseline entry '{full_name}' belongs to type '{owner}', which no \"sources\" "
                f"row declares. Add the workflow filter step and its sources row, or remove "
                f"the entry.")
    return sources, parsed


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
            observed[entry["FullName"]] = (entry.get("Statistics"), report)
    return observed, structural


def evaluate(baseline, observed):
    """Return (rows, breaches, structural) for the baselined set. Two-sided by design: the SLOWER
    side is the regression this exists to catch; the FASTER side is a stale baseline (ADR-0042 D6)
    or a benchmark whose work got optimised away, and both want a human."""
    rows = []
    breaches = []
    structural = []
    for full_name in sorted(baseline):
        mean_ns, band_pct = baseline[full_name]
        if full_name not in observed:
            structural.append(
                f"[{full_name}] is in baseline.json but absent from the parsed results. The "
                f"benchmark was removed, renamed, or never ran. Baseline expected {mean_ns:g} ns "
                f"+/- {band_pct:g}%.")
            continue
        stats, report = observed[full_name]
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
        delta_pct = (actual - mean_ns) / mean_ns * 100.0
        over = abs(delta_pct) - band_pct
        if over > 0:
            direction = "SLOWER" if delta_pct > 0 else "FASTER"
            verdict = f"BREACH ({direction})"
            breaches.append(
                f"[{full_name}] {direction} than baseline by {abs(delta_pct):.2f}%, outside the "
                f"+/-{band_pct:g}% band by {over:.2f} pp. baseline {mean_ns:g} ns, observed "
                f"{actual:.2f} ns.")
        else:
            verdict = "ok"
        rows.append({
            "name": full_name,
            "baseline_ns": mean_ns,
            "observed_ns": actual,
            "delta_pct": delta_pct,
            "band_pct": band_pct,
            "verdict": verdict,
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


def render_markdown(rows, breaches, structural, extras, enforce):
    mode = "ENFORCING" if enforce else "OBSERVING (reports, does not fail the job — §2.5)"
    out = ["## Perf regression vs `Tests/Verbara.Sdk.Benchmarks/baseline.json`", "",
           f"Mode: **{mode}**", ""]
    if structural:
        out += [f"### Structural failures ({len(structural)})", "",
                "A benchmark that never ran is not a pass (ADR-0042 D4).", ""]
        out += [f"- {item}" for item in structural] + [""]
    if breaches:
        out += [f"### Band breaches ({len(breaches)})", ""]
        out += [f"- {item}" for item in breaches] + [""]
    if rows:
        out += ["### Measured", "",
                "| benchmark | baseline (ns) | observed (ns) | delta | band | |",
                "|---|--:|--:|--:|--:|---|"]
        for row in rows:
            out.append(
                f"| `{_short(row['name'])}` | {row['baseline_ns']:g} | {row['observed_ns']:.2f} | "
                f"{row['delta_pct']:+.2f}% | ±{row['band_pct']:g}% | {row['verdict']} |")
        out.append("")
    if extras:
        out += [f"### Measured but not baselined ({len(extras)})", "",
                "Not a failure. Add a `benchmarks` entry once its hosted-runner spread is "
                "observed — see `baseline.README.md`.", ""]
        out += [f"- `{item}`" for item in extras] + [""]
    if not breaches and not structural:
        out += ["All baselined benchmarks are inside their bands.", ""]
    return "\n".join(out)


def main(argv):
    baseline_path, artifacts_root, markdown_out, enforce = parse_args(argv)
    sources, baseline = load_baseline(baseline_path)

    observed_raw, structural = collect_observed(artifacts_root, sources)
    rows, breaches, more_structural = evaluate(baseline, observed_raw)
    structural += more_structural
    extras = unbaselined(baseline, observed_raw)

    mode = "ENFORCING" if enforce else "OBSERVING"
    print(f"perf-baseline: mode={mode} baseline={baseline_path} artifacts={artifacts_root}")
    print(f"{'benchmark':<62} {'baseline':>12} {'observed':>12} {'delta':>9} {'band':>7}  verdict")
    for row in rows:
        print(f"{_short(row['name']):<62} {row['baseline_ns']:>12.2f} {row['observed_ns']:>12.2f} "
              f"{row['delta_pct']:>+8.2f}% {row['band_pct']:>6.0f}%  {row['verdict']}")

    level = "error" if enforce else "warning"
    for item in structural:
        print(f"::{level}::perf-baseline STRUCTURAL: {item}")
    for item in breaches:
        print(f"::{level}::perf-baseline BREACH: {item}")
    for item in extras:
        print(f"::notice::perf-baseline: '{item}' is measured but has no baseline entry.")

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
        print(f"perf-baseline: OK — {len(rows)} benchmark(s) inside their bands.")
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
