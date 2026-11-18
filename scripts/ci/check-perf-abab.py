#!/usr/bin/env python3
"""Compare HEAD against a reference tag measured in the same job (the in-job ABAB comparison).

Usage:
  check-perf-abab.py --baseline <baseline.json> --abab-root <dir> [--enforce]
                     [--markdown-out <file>] [--json-out <file>]

WHAT THIS IS. The absolute gate (check-perf-baseline.py) compares a run against hosted-runner
means committed in baseline.json. The hosted fleet mixes CPU models that read the same code up to
2-3x apart, so its bands are wide enough that a 10-20% time regression is not seen on any host.
This comparison removes the host from the question: the weekly job builds the reference tag
declared in baseline.json `abab.reference_tag` beside HEAD and runs both, alternated, in the same
job on the same machine (scripts/ci/run-perf-abab.sh). Time is judged by the ratio HEAD/reference.

THE ORDER AND THE ROUNDS. The declared order (`abab.order`, R H H R R H) has as many R as H
invocations: round k pairs the k-th R invocation with the k-th H invocation, so round 1 =
(position 1, position 2), round 2 = (position 4, position 3) and round 3 = (position 5,
position 6). Nothing here is specific to three rounds.

THE LAUNCHES. Each invocation runs every case in `abab.launch_count` processes (two):
run-perf-abab.sh passes `--launchCount` to the in-job run only, and BenchmarkDotNet rewrites the
source's `[ShortRunJob]` in place (`ShortRun(IterationCount=3, LaunchCount=2, WarmupCount=3)`, one
job, N = 6). Every case of every invocation must show it: its job text (the DisplayInfo after ': ')
states `LaunchCount=<launch_count>`, and its `Measurements` hold Result iterations from launches 1
to launch_count only, IterationCount of them in each. Anything else is STRUCTURAL: the override did
not apply, or applied differently, and the run measured another design than the one declared.

THE STATISTIC. For each case and round k, r_k = mean(H_k) / mean(R_k) (BenchmarkDotNet
`Statistics.Mean`, nanoseconds, over the Result iterations of all the invocation's launches), and
g is the geometric mean of the r_k. In-job noise is between processes, one launch-level spike at a
time, so the verdict reads g and asks a majority of the rounds to confirm it: SLOWER when g > slower_ratio x accepted and more than half of the r_k are
over that bar; FASTER when g < faster_ratio x accepted and more than half of the r_k are under it.
A bar is SUSPECTED when g is beyond it or at least half of the r_k are; a suspicion that is not a
verdict is UNCONFIRMED; ok otherwise. With three rounds that is "exactly one of g and the majority";
with two it is the rule of the two-round design (SLOWER only when both rounds are over,
UNCONFIRMED when one is). The r_k beyond a bar in an ok row are its lone rounds: counted, never a
finding. `accepted` is 1 unless `abab.accepted` records a cost measured against this same
reference. A case whose mean is under
`min_mean_ns` in a HEAD invocation is shown and not judged: a ratio of a few cycles measures
BenchmarkDotNet's overhead subtraction, not the code. A case under the floor in the reference only
is judged one way, by the same rule on mean(H_k) / max(mean(R_k), min_mean_ns) against
`sub_floor_slower_ratio`; it is never FASTER, and it is left out of the noise statistics, which
read g (median and p95 of |ln g|, share of g outside the bars) beside the median |ln r_k|.

BYTES IN THE JOB. Both arms run on the same machine, so on the same core count, and a bytes
difference between them is the code. For a case with a `bytes` entry in baseline.json `benchmarks`,
MORE-BYTES when every round's delta exceeds its `bytes_tolerance`, FEWER-BYTES when every round's
delta is under minus it: bytes are deterministic, so no majority applies to them. A case with no
`bytes` entry shows its delta and is not judged. An exactly gated case whose bytes differ between
the rounds of one arm is a bytes anomaly (reported).

NEVER DROPPED. The two `--list flat` outputs are the methods each tree declares. A method in one
list only is listed as HEAD only or reference only; a parameter case present in one arm only is
listed as a case-set difference. A case its arm declares but some invocation did not measure, a
declared class with no report, a mean that is not a finite number, an invocation that timed out, or
invocations that disagree on SDK, runtime, core count, JIT ISA, hardware intrinsics,
BenchmarkDotNet version or processor, and a reference whose commit is not recorded or is not the
tag's, are STRUCTURAL failures: the comparison did not happen as declared.

NOT COMPARED IS A FINDING. A case whose class source differs from the reference, or whose
BenchmarkDotNet job (the text after ': ' in its DisplayInfo) differs between invocations or is not
recorded, is shown and not judged, and counts as a finding (`abab_blind`, status breach). A class
with cases on both sides of which none is judged on time or bytes, and a run that judged no row on
time, are STRUCTURAL: an edit to a benchmark cannot turn the comparison into a silent pass.

OBSERVING OR ENFORCING. `PERF_ABAB_ENFORCE` (or --enforce) selects the mode, and nothing else
does. It must be exactly 'true' or 'false' (or unset, observing): any other spelling is exit 2,
because the workflow ties the step's continue-on-error to the exact string 'true'.
`PERF_GATE_ENFORCE`, the absolute gate's switch, is not read here. Observing, every finding is
reported and the exit is 0. Enforcing, SLOWER, MORE-BYTES and structural failures exit 1.

Exit codes:
  0  pass; or only FASTER / FEWER-BYTES / UNCONFIRMED / not-judged / one-side-only findings; or
     observing with findings of any kind
  1  enforcing and at least one SLOWER, MORE-BYTES or structural failure
  2  the guard is misconfigured, in both modes: bad usage, a PERF_ABAB_ENFORCE other than
     'true' or 'false', a baseline.json the absolute
     comparator refuses, an invalid or missing `abab` block, or a missing or invalid manifest

Stdlib only. One definition of a measurement: `load_baseline`, `_measurement`,
`_observed_environment`, `_observed_bytes` and `ISA_PATTERN` come from check-perf-baseline.py.
Unit-tested by scripts/tests/test_check_perf_abab.py in the always-on `Coverage Script Tests` job.
"""
import importlib.util
import json
import math
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_SPEC = importlib.util.spec_from_file_location(
    "check_perf_baseline", os.path.join(_HERE, "check-perf-baseline.py"))
_BASE = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_BASE)

SWITCH = "PERF_ABAB_ENFORCE"
REPORT_SUFFIX = _BASE.REPORT_SUFFIX
BENCHMARK_DIR = "Tests/Verbara.Sdk.Benchmarks/"
# Files of the benchmark project that change with every re-baseline and compile into nothing.
PROJECT_EXEMPT = {"baseline.json", "baseline.README.md", "performance-record.json", "README.md"}
_TAG = re.compile(r"v[0-9]+\.[0-9]+\.[0-9]+")
_LIST_LINE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+")
_ABAB_KEYS = {"reference_tag", "order", "slower_ratio", "faster_ratio", "min_mean_ns",
              "sub_floor_slower_ratio", "invocation_timeout_s", "launch_count", "rows",
              "accepted"}
_LAUNCH_COUNT = re.compile(r"\bLaunchCount=(\d+)\b")
_ITERATION_COUNT = re.compile(r"\bIterationCount=(\d+)\b")
_ROW_KEYS = {"slower_ratio", "faster_ratio", "judge_time", "reason"}
_ACCEPTED_KEYS = {"ratio", "relative_to", "pr", "reason"}


def die(message):
    """Guard-is-broken exit (2), in both modes."""
    print(f"::error::perf-abab: {message}")
    sys.exit(2)


# The absolute comparator's validation exits through its own `die`; route it through ours so the
# annotation names this guard.
_BASE.die = die
_is_number = _BASE._is_number


def _method_of(full_name):
    return _BASE._PARAMETERS.sub("", full_name)


def parse_args(argv):
    baseline = root = markdown_out = json_out = None
    raw = os.environ.get(SWITCH)
    if raw is None or raw == "" or raw == "false":
        enforce = False
    elif raw == "true":
        enforce = True
    else:
        # The workflow arms continue-on-error on the exact string 'true'; any other spelling
        # would read ENFORCING here while the step's failure still left the job green.
        die(f"{SWITCH} is {raw!r}: it must be exactly 'true' or 'false' (or unset), the only "
            f"two values .github/workflows/perf-regression.yml compares it with.")
    usage = ("usage: check-perf-abab.py --baseline <baseline.json> --abab-root <dir> [--enforce] "
             "[--markdown-out <file>] [--json-out <file>]")
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == "--enforce":
            enforce = True
        elif arg in ("--baseline", "--abab-root", "--markdown-out", "--json-out") \
                and i + 1 < len(argv):
            i += 1
            if arg == "--baseline":
                baseline = argv[i]
            elif arg == "--abab-root":
                root = argv[i]
            elif arg == "--markdown-out":
                markdown_out = argv[i]
            else:
                json_out = argv[i]
        else:
            die(f"unrecognized or incomplete argument '{arg}'. {usage}")
        i += 1
    if not baseline or not root:
        die(usage)
    return baseline, root, markdown_out, json_out, enforce


def _ratio_value(value, name, low=None, high=None):
    if not _is_number(value):
        die(f"baseline \"abab\" {name} must be a number, not {value!r}.")
    if low is not None and not value > low:
        die(f"baseline \"abab\" {name} must be greater than {low:g}, not {value!r}.")
    if high is not None and not value < high:
        die(f"baseline \"abab\" {name} must be less than {high:g}, not {value!r}.")
    return float(value)


def load_abab(path, types):
    """The `abab` block, validated. Every problem is exit 2: a comparison whose rules the guard
    misreads cannot be called a pass or a breach."""
    try:
        with open(path, encoding="utf-8") as handle:
            doc = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        die(f"cannot read baseline '{path}': {exc}")
    block = doc.get("abab") if isinstance(doc, dict) else None
    if not isinstance(block, dict):
        die(f"baseline '{path}' has no \"abab\" object: the in-job comparison has no declared "
            f"reference tag, order or thresholds.")
    unknown = sorted(k for k in block if not k.startswith("_") and k not in _ABAB_KEYS)
    if unknown:
        die(f"baseline \"abab\" has unknown key(s) {unknown}: the comparator would not read them.")
    missing = sorted(k for k in _ABAB_KEYS if k not in block)
    if missing:
        die(f"baseline \"abab\" lacks {missing}.")
    tag = block["reference_tag"]
    if not isinstance(tag, str) or not _TAG.fullmatch(tag):
        die(f"baseline \"abab.reference_tag\" must be a release tag such as \"v2.10.1\", not "
            f"{tag!r}.")
    order = block["order"]
    if not isinstance(order, list) or any(arm not in ("R", "H") for arm in order) \
            or order.count("R") != order.count("H") or order.count("R") < 2:
        die(f"baseline \"abab.order\" must hold \"R\" and \"H\" in equal numbers, at least two "
            f"of each (one per round), not {order!r}.")
    slower = _ratio_value(block["slower_ratio"], "slower_ratio", low=1.0)
    faster = _ratio_value(block["faster_ratio"], "faster_ratio", low=0.0, high=1.0)
    floor = block["min_mean_ns"]
    if not _is_number(floor) or floor < 0:
        die(f"baseline \"abab.min_mean_ns\" must be a non-negative number, not {floor!r}.")
    sub_floor = _ratio_value(block["sub_floor_slower_ratio"], "sub_floor_slower_ratio",
                             low=slower)
    timeout = block["invocation_timeout_s"]
    if not isinstance(timeout, int) or isinstance(timeout, bool) or timeout <= 0:
        die(f"baseline \"abab.invocation_timeout_s\" must be a positive integer, not {timeout!r}.")
    launches = block["launch_count"]
    if not isinstance(launches, int) or isinstance(launches, bool) or launches <= 0:
        die(f"baseline \"abab.launch_count\" must be a positive integer, not {launches!r}.")

    rows = block["rows"]
    if not isinstance(rows, dict):
        die("baseline \"abab.rows\" must be an object keyed by BenchmarkDotNet FullName.")
    overrides = {}
    for name, entry in rows.items():
        if name.startswith("_"):
            continue
        _owned(name, types, "rows")
        if not isinstance(entry, dict):
            die(f"baseline \"abab.rows\" entry '{name}' must be an object.")
        bad = sorted(k for k in entry if k not in _ROW_KEYS)
        if bad:
            die(f"baseline \"abab.rows\" entry '{name}' has unknown key(s) {bad}.")
        _reason(entry, f"\"abab.rows\" entry '{name}'")
        override = {"reason": entry["reason"].strip()}
        if "slower_ratio" in entry:
            override["slower_ratio"] = _ratio_value(entry["slower_ratio"],
                                                    f"rows '{name}' slower_ratio", low=1.0)
        if "faster_ratio" in entry:
            override["faster_ratio"] = _ratio_value(entry["faster_ratio"],
                                                    f"rows '{name}' faster_ratio", low=0.0,
                                                    high=1.0)
        if "judge_time" in entry:
            if not isinstance(entry["judge_time"], bool):
                die(f"baseline \"abab.rows\" entry '{name}' judge_time must be true or false.")
            override["judge_time"] = entry["judge_time"]
        if len(override) == 1:
            die(f"baseline \"abab.rows\" entry '{name}' overrides nothing.")
        overrides[name] = override

    accepted_block = block["accepted"]
    if not isinstance(accepted_block, dict):
        die("baseline \"abab.accepted\" must be an object keyed by BenchmarkDotNet FullName.")
    accepted = {}
    for name, entry in accepted_block.items():
        if name.startswith("_"):
            continue
        _owned(name, types, "accepted")
        if not isinstance(entry, dict):
            die(f"baseline \"abab.accepted\" entry '{name}' must be an object.")
        bad = sorted(k for k in entry if k not in _ACCEPTED_KEYS)
        if bad:
            die(f"baseline \"abab.accepted\" entry '{name}' has unknown key(s) {bad}.")
        _reason(entry, f"\"abab.accepted\" entry '{name}'")
        ratio = _ratio_value(entry.get("ratio"), f"accepted '{name}' ratio", low=0.0)
        if entry.get("relative_to") != tag:
            die(f"baseline \"abab.accepted\" entry '{name}' has relative_to "
                f"{entry.get('relative_to')!r}, not the reference tag {tag}: an accepted cost is "
                f"a ratio against one reference and means nothing against another. Remove it "
                f"when the reference tag moves.")
        accepted[name] = ratio
    return {"reference_tag": tag, "order": list(order), "slower_ratio": slower,
            "faster_ratio": faster, "min_mean_ns": float(floor),
            "sub_floor_slower_ratio": sub_floor, "invocation_timeout_s": timeout,
            "launch_count": launches, "rows": overrides, "accepted": accepted}


def _owned(name, types, where):
    owner = _BASE._owning_type(name)
    if owner not in types:
        die(f"baseline \"abab.{where}\" entry '{name}' belongs to type '{owner}', which no "
            f"\"sources\" row declares, so it would never be compared.")


def _reason(entry, where):
    reason = entry.get("reason")
    if not isinstance(reason, str) or not reason.strip():
        die(f"baseline {where} needs a non-empty \"reason\".")


def load_manifest(root, abab):
    path = os.path.join(root, "manifest.json")
    try:
        with open(path, encoding="utf-8") as handle:
            manifest = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        die(f"cannot read the run manifest '{path}': {exc}. run-perf-abab.sh writes it after "
            f"every run; without it nothing says which invocation was which arm.")
    if not isinstance(manifest, dict) or manifest.get("schema") != 1:
        die(f"manifest '{path}' is not a schema-1 object.")
    if manifest.get("order") != abab["order"]:
        die(f"manifest order {manifest.get('order')!r} is not the declared abab.order "
            f"{abab['order']!r}: the run's own order is not trusted.")
    if manifest.get("launch_count") != abab["launch_count"]:
        die(f"manifest launch_count {manifest.get('launch_count')!r} is not the declared "
            f"abab.launch_count {abab['launch_count']}: the run's own launch count is not "
            f"trusted.")
    if manifest.get("reference_tag") != abab["reference_tag"]:
        die(f"manifest reference_tag {manifest.get('reference_tag')!r} is not the declared "
            f"{abab['reference_tag']}.")
    invocations = manifest.get("invocations")
    if not isinstance(invocations, list) or len(invocations) != len(abab["order"]):
        die(f"manifest '{path}' must list one invocation per position of the order.")
    for index, (inv, arm) in enumerate(zip(invocations, abab["order"]), start=1):
        if not isinstance(inv, dict) or inv.get("position") != index or inv.get("arm") != arm \
                or inv.get("dir") != f"{index}-{arm}":
            die(f"manifest invocation #{index} is {inv!r}, not position {index}, arm {arm}, "
                f"dir {index}-{arm}.")
    for key in ("changed_benchmark_paths", "changed_compiled_paths"):
        value = manifest.get(key)
        if value is not None and (not isinstance(value, list)
                                  or not all(isinstance(p, str) for p in value)):
            die(f"manifest {key} must be a list of paths or null.")
    return manifest


def read_list(root, arm, types):
    """(methods, problem): the method FullNames of the declared types in `list-<arm>.txt`."""
    path = os.path.join(root, f"list-{arm}.txt")
    try:
        with open(path, encoding="utf-8", errors="replace") as handle:
            lines = handle.read().splitlines()
    except OSError:
        return set(), f"list-{arm}.txt is missing: nothing says which methods arm {arm} declares."
    methods = {line.strip() for line in lines
               if _LIST_LINE.fullmatch(line.strip())
               and line.strip().rsplit(".", 1)[0] in types}
    if not methods:
        return set(), (f"list-{arm}.txt names no method of a declared type: the `--list flat` "
                       f"run of arm {arm} failed or its filters matched nothing.")
    return methods, None


def _launch_problem(entry, launch_count):
    """Why a case's report does not show `launch_count` launches, or None when it does."""
    job = _job_config(entry)
    if job is not None:
        stated = _LAUNCH_COUNT.search(job)
        if stated is None:
            return f"its job '{job}' states no LaunchCount"
        if int(stated.group(1)) != launch_count:
            return f"its job '{job}' states LaunchCount={stated.group(1)}"
    measurements = entry.get("Measurements")
    if not isinstance(measurements, list):
        return "it records no Measurements, so its launches cannot be counted"
    per_launch = {}
    for item in measurements:
        if isinstance(item, dict) and item.get("IterationMode") == "Workload" \
                and item.get("IterationStage") == "Result":
            per_launch[item.get("LaunchIndex")] = per_launch.get(item.get("LaunchIndex"), 0) + 1
    expected = set(range(1, launch_count + 1))
    if set(per_launch) != expected:
        shown = ", ".join(str(k) for k in sorted(per_launch, key=str)) or "none"
        return (f"its Result iterations come from launch(es) {shown}, not 1 to "
                f"{launch_count}")
    iterations = _ITERATION_COUNT.search(job) if job is not None else None
    if iterations is not None:
        want = int(iterations.group(1))
        short = {k: v for k, v in per_launch.items() if v != want}
        if short:
            return (f"launch(es) {', '.join(str(k) for k in sorted(short))} hold "
                    f"{', '.join(str(short[k]) for k in sorted(short))} Result iteration(s), "
                    f"not IterationCount={want}")
    return None


def read_invocation(root, inv, sources, environment, jit_isa, launch_count):
    """Every declared type's report in one invocation directory. Returns (reports, structural):
    reports maps type -> {"cases": {FullName: entry}, "host": HostEnvironmentInfo}."""
    label = inv["dir"]
    directory = os.path.join(root, label)
    structural = []
    reports = {}
    off_launch = []
    if inv.get("timed_out") is True:
        structural.append(f"[{label}] the invocation timed out (exit {inv.get('exit_code')}): "
                          f"its reports, if any, are a partial run.")
    if not os.path.isdir(directory):
        structural.append(f"[{label}] the invocation produced no directory: it never ran.")
        return reports, structural
    for type_name in sorted(set(sources.values())):
        report = os.path.join(directory, "results", f"{type_name}{REPORT_SUFFIX}")
        try:
            with open(report, encoding="utf-8") as handle:
                doc = json.load(handle)
        except FileNotFoundError:
            continue
        except (OSError, json.JSONDecodeError) as exc:
            structural.append(f"[{label}] report of {type_name} is unreadable: {exc}")
            continue
        entries = doc.get("Benchmarks") if isinstance(doc, dict) else None
        if not isinstance(entries, list):
            structural.append(f"[{label}] report of {type_name} has no \"Benchmarks\" array.")
            continue
        cases = {}
        for entry in entries:
            name = entry.get("FullName") if isinstance(entry, dict) else None
            if not isinstance(name, str) or not name:
                structural.append(f"[{label}] report of {type_name} has an entry with no "
                                  f"\"FullName\".")
                continue
            cases[name] = entry
            problem = _launch_problem(entry, launch_count)
            if problem:
                off_launch.append((name, problem))
        reports[type_name] = {"cases": cases, "host": doc.get("HostEnvironmentInfo")}
    if off_launch:
        name, problem = off_launch[0]
        structural.append(f"[{label}] {len(off_launch)} case(s) do not show the declared "
                          f"abab.launch_count {launch_count}: the --launchCount override did not "
                          f"apply as declared, so the invocation measured another design. First: "
                          f"{name}: {problem}.")
    if jit_isa and reports:
        seen = set()
        for name in sorted(os.listdir(directory)):
            if name.endswith(".log"):
                try:
                    with open(os.path.join(directory, name), encoding="utf-8",
                              errors="replace") as handle:
                        seen.update(_BASE.ISA_PATTERN.findall(handle.read()))
                except OSError:
                    continue
        if not seen:
            structural.append(f"[{label}] no BenchmarkDotNet log names the JIT ISA, so the pin to "
                              f"{jit_isa} cannot be confirmed.")
        elif seen != {jit_isa}:
            structural.append(f"[{label}] the JIT targeted {', '.join(sorted(seen))}, not the "
                              f"pinned {jit_isa}.")
    return reports, structural


def check_hosts(invocation_reports, environment):
    """Structural failures when the invocations were not one experiment: each against the declared
    environment, and all against each other on harness and processor."""
    structural = []
    observed = {}
    for label, reports in invocation_reports:
        for type_name, report in sorted(reports.items()):
            host = report["host"]
            sdk, runtime, cores = _BASE._observed_environment(host)
            host = host if isinstance(host, dict) else {}
            values = {"SDK": sdk, "runtime": runtime, "logical core count": cores,
                      "BenchmarkDotNet version": host.get("BenchmarkDotNetVersion"),
                      "processor": host.get("ProcessorName")}
            for what, value in values.items():
                observed.setdefault(what, {}).setdefault(label, set()).add(value)
            for entry in report["cases"].values():
                intrinsics = entry.get("HardwareIntrinsics") if isinstance(entry, dict) else None
                observed.setdefault("hardware intrinsics", {}).setdefault(label, set()).add(
                    intrinsics if isinstance(intrinsics, str) and intrinsics else None)
    declared = {}
    if environment:
        declared = {"SDK": environment["dotnet_sdk"], "runtime": environment["dotnet_runtime"],
                    "logical core count": environment["logical_cores"]}
    for what in ("SDK", "runtime", "logical core count", "BenchmarkDotNet version", "processor",
                 "hardware intrinsics"):
        per_label = observed.get(what, {})
        for label, values in sorted(per_label.items()):
            if None in values:
                structural.append(f"[{label}] a report does not record its {what}.")
            elif what in declared and values != {declared[what]}:
                shown = ", ".join(sorted(_BASE._shown(v) for v in values))
                structural.append(f"[{label}] measured on {what} {shown}, not the declared "
                                  f"{declared[what]}.")
        distinct = set().union(*per_label.values()) - {None} if per_label else set()
        if len(distinct) > 1:
            by = "; ".join(f"{label}: {', '.join(sorted(str(v) for v in values))}"
                           for label, values in sorted(per_label.items()))
            structural.append(f"the invocations disagree on the {what} ({by}): the two arms were "
                              f"not measured as one experiment.")
    return structural, {what: sorted({str(v) for vals in per.values() for v in vals
                                      if v is not None})
                        for what, per in observed.items()}


def _geomean(values):
    return math.exp(sum(math.log(v) for v in values) / len(values))


def _median(values):
    ordered = sorted(values)
    n = len(ordered)
    if n == 0:
        return None
    mid = n // 2
    return ordered[mid] if n % 2 else (ordered[mid - 1] + ordered[mid]) / 2


def _p95(values):
    """Nearest-rank 95th percentile."""
    ordered = sorted(values)
    if not ordered:
        return None
    return ordered[max(0, math.ceil(0.95 * len(ordered)) - 1)]


def compare(abab, baseline, manifest, lists, invocations, positions):
    """Classify and judge every case. `invocations` maps label -> reports; `positions` maps arm ->
    the labels of that arm in round order."""
    rows = []
    structural = []
    changed = manifest.get("changed_benchmark_paths")
    changed_sources = set()
    if changed is None:
        structural.append("the manifest does not record which benchmark files differ from the "
                          "reference, so no row can be judged as the same benchmark on both "
                          "sides.")
    else:
        changed_sources = {p[len(BENCHMARK_DIR):-3] for p in changed
                           if p.startswith(BENCHMARK_DIR) and p.endswith(".cs")
                           and "/" not in p[len(BENCHMARK_DIR):]}

    # What each arm measured, per invocation, per case.
    measured = {arm: [] for arm in ("R", "H")}
    for arm in ("R", "H"):
        for label in positions[arm]:
            cases = {}
            for type_name, report in invocations[label].items():
                for name, entry in report["cases"].items():
                    cases[name] = entry
            measured[arm].append((label, cases))

    # Presence, per arm: the arm's list is what it declares.
    complete = {"R": {}, "H": {}}   # arm -> case -> [entry per round]
    incomplete = {}                 # case -> message
    for arm in ("R", "H"):
        declared = lists[arm]
        reported_types = [{_BASE._owning_type(n) for n in cases} | set(invocations[label])
                          for label, cases in measured[arm]]
        for (label, _cases), types_here in zip(measured[arm], reported_types):
            for type_name in sorted({m.rsplit(".", 1)[0] for m in declared} - types_here):
                structural.append(f"[{label}] {type_name} has methods in list-{arm}.txt and no "
                                  f"report in this invocation: the class did not run.")
        all_cases = set()
        for label, cases in measured[arm]:
            all_cases.update(cases)
        for name in sorted(all_cases):
            if _method_of(name) not in declared:
                structural.append(f"[{name}] is measured by arm {arm} but list-{arm}.txt does "
                                  f"not declare it: the list and the run disagree.")
        for method in sorted(declared):
            type_name = method.rsplit(".", 1)[0]
            cases = sorted(n for n in all_cases if _method_of(n) == method)
            if not cases:
                if any(type_name in invocations[label] for label in positions[arm]):
                    structural.append(f"[{method}] is declared by list-{arm}.txt and measured in "
                                      f"no invocation of arm {arm}.")
                continue
            for name in cases:
                entries = []
                for label, here in measured[arm]:
                    if name in here:
                        entries.append(here[name])
                    elif type_name in invocations[label]:
                        entries.append(None)
                        message = (f"[{name}] is measured by arm {arm} but missing from "
                                   f"invocation {label}.")
                        structural.append(message)
                        incomplete.setdefault(name, message)
                    else:
                        entries.append(None)
                        incomplete.setdefault(name, f"[{name}] invocation {label} has no report "
                                                    f"of its class.")
                if all(e is not None for e in entries):
                    complete[arm][name] = entries

    # Means, bytes, and per-case one-sidedness.
    names = sorted(set(complete["R"]) | set(complete["H"]) | set(incomplete))
    for name in names:
        method = _method_of(name)
        in_r, in_h = method in lists["R"], method in lists["H"]
        row = {"name": name, "class": "both", "arm": None, "judged": False,
               "not_judged_reason": None, "means": {"R": [], "H": []}, "ratios": [],
               "geomean": None, "self": {"R": None, "H": None}, "bytes": {"R": [], "H": []},
               "bytes_delta": [], "bytes_judged": False, "bytes_verdict": None,
               "thresholds": None, "verdict": "not judged", "blind": False,
               "lone_rounds": 0}
        bad_mean = False
        for arm in ("R", "H"):
            for entry in complete[arm].get(name, []):
                mean, reason = _BASE._measurement(entry)
                if mean is None:
                    structural.append(f"[{name}] arm {arm}: {reason}, so it carries no "
                                      f"measurement.")
                    bad_mean = True
                row["means"][arm].append(mean)
                allocated = _BASE._observed_bytes(entry)
                row["bytes"][arm].append(None if allocated is None else _int_or(allocated))
        if name in incomplete:
            row["class"] = "incomplete"
            row["not_judged_reason"] = "missing in a round (structural)"
        elif in_h and not in_r:
            row["class"], row["arm"] = "head-only", "H"
            row["not_judged_reason"] = "no reference: the method is new or renamed since the tag"
        elif in_r and not in_h:
            row["class"], row["arm"] = "reference-only", "R"
            row["not_judged_reason"] = "removed or renamed since the reference tag"
        elif name not in complete["R"] or name not in complete["H"]:
            row["class"] = "case-set-differs"
            row["arm"] = "R" if name in complete["R"] else "H"
            row["not_judged_reason"] = f"this parameter case exists in arm {row['arm']} only"
        elif bad_mean:
            row["class"] = "incomplete"
            row["not_judged_reason"] = "a mean is not a finite number (structural)"
        else:
            configs = {_job_config(e) for arm in ("R", "H") for e in complete[arm][name]}
            _judge(row, abab, baseline, changed_sources, configs, structural)
        rows.append(row)

    # Blindness. A class whose cases exist on both sides and none of which is judged on time or
    # bytes is not compared at all; nor is a run that judged no row. Both are structural, so a
    # changed benchmark source or job cannot read "pass" with nothing behind it.
    by_type = {}
    for row in rows:
        if row["class"] == "both":
            by_type.setdefault(_BASE._owning_type(row["name"]), []).append(row)
    for type_name, members in sorted(by_type.items()):
        if not any(r["judged"] or r["bytes_judged"] for r in members):
            reasons = sorted({r["not_judged_reason"] for r in members if r["not_judged_reason"]})
            structural.append(f"[{type_name}] has {len(members)} case(s) on both sides and none "
                              f"is judged on time or bytes ({'; '.join(reasons)}): the class is "
                              f"not compared. Move abab.reference_tag to a release that carries "
                              f"this benchmark as HEAD has it.")
    if not any(row["judged"] for row in rows):
        structural.append("no row was judged on time: the comparison measured nothing it could "
                          "call a pass.")
    return rows, structural


def _job_config(entry):
    """The BenchmarkDotNet job a case ran under: the text after ': ' in its DisplayInfo, e.g.
    'ShortRun(IterationCount=3, LaunchCount=1, WarmupCount=3)'. None when not recorded."""
    display = entry.get("DisplayInfo") if isinstance(entry, dict) else None
    if not isinstance(display, str) or ": " not in display:
        return None
    return display.split(": ", 1)[1].strip()


def _int_or(value):
    return int(value) if float(value).is_integer() else value


def _bar(ratios, g, beyond):
    """(verdict, suspected) for one bar. `beyond(x)` says whether x is past the bar. The verdict
    needs g past it and a strict majority of the rounds past it; a suspicion needs either g past
    it or at least half of the rounds. With three rounds both halves read "two of three"; with two,
    a verdict needs both rounds and one round is a suspicion, which is the rule two rounds always
    had."""
    count = sum(1 for r in ratios if beyond(r))
    verdict = beyond(g) and count * 2 > len(ratios)
    return verdict, verdict or beyond(g) or count * 2 >= len(ratios)


def _verdict(row, ratios, g, slower, faster):
    slow, slow_suspected = _bar(ratios, g, lambda x: x > slower)
    fast, fast_suspected = (False, False) if faster is None else \
        _bar(ratios, g, lambda x: x < faster)
    if slow:
        row["verdict"] = "SLOWER"
    elif fast:
        row["verdict"] = "FASTER"
    elif slow_suspected or fast_suspected:
        row["verdict"] = "UNCONFIRMED"
    else:
        row["verdict"] = "ok"
        # One process spike is what the majority exists to absorb: counted, never a finding.
        row["lone_rounds"] = sum(1 for r in ratios
                                 if r > slower or (faster is not None and r < faster))


def _judge(row, abab, baseline, changed_sources, configs, structural):
    name = row["name"]
    rmeans, hmeans = row["means"]["R"], row["means"]["H"]
    override = abab["rows"].get(name, {})
    accepted = abab["accepted"].get(name, 1.0)
    slower = override.get("slower_ratio", abab["slower_ratio"]) * accepted
    faster = override.get("faster_ratio", abab["faster_ratio"]) * accepted
    floor = abab["min_mean_ns"]
    row["thresholds"] = {"slower": slower, "faster": faster, "accepted": accepted}
    positive = all(m > 0 for m in rmeans + hmeans)
    if positive:
        row["ratios"] = [h / r for r, h in zip(rmeans, hmeans)]
        row["geomean"] = _geomean(row["ratios"])
        row["self"] = {"R": rmeans[-1] / rmeans[0], "H": hmeans[-1] / hmeans[0]}
    short = _BASE._owning_type(name).rsplit(".", 1)[-1]
    source_changed = short in changed_sources
    blind = None
    if source_changed:
        blind = ("the benchmark source differs from the reference: the ratio includes the "
                 "benchmark's own change")
    elif None in configs:
        blind = "an invocation does not record the case's BenchmarkDotNet job (DisplayInfo)"
    elif len(configs) > 1:
        blind = (f"the arms ran the case under different BenchmarkDotNet jobs "
                 f"({' / '.join(sorted(configs))}): the ratio includes the harness")
    reason = blind
    sub_floor = False
    if reason is None and override.get("judge_time") is False:
        reason = f"time not judged by abab.rows: {override['reason']}"
    elif reason is None and (min(rmeans + hmeans) < floor or not positive):
        if min(hmeans) >= floor and all(r >= 0 for r in rmeans):
            # Only the reference is under the floor: its ratio measures the overhead
            # subtraction, but HEAD leaving the floor by a coarse factor is the code. Judged one
            # way only, against the floor itself.
            sub_floor = True
        else:
            reason = (f"a mean is under the {floor:g} ns floor: the ratio would measure "
                      f"BenchmarkDotNet's overhead subtraction")
    row["blind"] = blind is not None
    if reason:
        row["not_judged_reason"] = reason
    elif sub_floor:
        bar = abab["sub_floor_slower_ratio"]
        row["judged"] = True
        row["sub_floor"] = True
        row["thresholds"] = {"slower": bar, "faster": None, "accepted": 1.0}
        against = [h / max(r, floor) for r, h in zip(rmeans, hmeans)]
        row["floor_ratios"] = against
        row["floor_geomean"] = _geomean(against)
        _verdict(row, against, row["floor_geomean"], bar, None)
    else:
        row["judged"] = True
        _verdict(row, row["ratios"], row["geomean"], slower, faster)

    # Bytes.
    rbytes, hbytes = row["bytes"]["R"], row["bytes"]["H"]
    if all(b is not None for b in rbytes + hbytes):
        row["bytes_delta"] = [_int_or(h - r) for r, h in zip(rbytes, hbytes)]
    expected = baseline.get(name)
    gated = expected is not None and expected["bytes"] is not None
    if not gated:
        row["bytes_verdict"] = "not judged"
        return
    if not row["bytes_delta"]:
        structural.append(f"[{name}] is gated on bytes but an invocation carries no numeric "
                          f"\"Memory.BytesAllocatedPerOperation\": the memory diagnoser is off.")
        row["bytes_verdict"] = "not judged"
        return
    if blind:
        row["bytes_verdict"] = "not judged"
        return
    bound = expected["bytes_tolerance"]
    row["bytes_judged"] = True
    if all(d > bound for d in row["bytes_delta"]):
        row["bytes_verdict"] = "MORE-BYTES"
    elif all(d < -bound for d in row["bytes_delta"]):
        row["bytes_verdict"] = "FEWER-BYTES"
    else:
        row["bytes_verdict"] = "ok"
    if bound == 0 and (len(set(rbytes)) > 1 or len(set(hbytes)) > 1):
        row["bytes_anomaly"] = True


def summarize(rows, structural):
    judged = [row for row in rows if row["judged"] and not row.get("sub_floor")]
    logs = [abs(math.log(r)) for row in judged for r in row["ratios"]]
    g_logs = [abs(math.log(row["geomean"])) for row in judged]
    selfs = [abs(math.log(row["self"][arm])) for row in judged for arm in ("R", "H")]
    outside = sum(1 for row in judged
                  if row["geomean"] > row["thresholds"]["slower"]
                  or row["geomean"] < row["thresholds"]["faster"])
    count = lambda key, value: sum(1 for row in rows if row[key] == value)  # noqa: E731
    slower, more = count("verdict", "SLOWER"), count("bytes_verdict", "MORE-BYTES")
    return {
        "judged_rows": sum(1 for row in rows if row["judged"]),
        "sub_floor_judged": sum(1 for row in rows if row.get("sub_floor")),
        "blind": sum(1 for row in rows if row.get("blind")),
        "ratios_count": len(logs),
        "median_abs_log_ratio": _median(logs),
        "geomeans_count": len(g_logs),
        "median_abs_log_geomean": _median(g_logs),
        "p95_abs_log_geomean": _p95(g_logs),
        "share_geomean_outside_band": outside / len(g_logs) if g_logs else None,
        "lone_rounds": sum(row["lone_rounds"] for row in rows),
        "median_abs_log_self": _median(selfs),
        "order_effect_geomean": _geomean([row["self"]["R"] for row in judged]) if judged else None,
        "slower": slower,
        "faster": count("verdict", "FASTER"),
        "unconfirmed": count("verdict", "UNCONFIRMED"),
        "more_bytes": more,
        "fewer_bytes": count("bytes_verdict", "FEWER-BYTES"),
        "bytes_anomalies": sum(1 for row in rows if row.get("bytes_anomaly")),
        "structural": len(structural),
        "failing": slower + more + len(structural),
        "head_only": count("class", "head-only"),
        "reference_only": count("class", "reference-only"),
        "case_set_differs": count("class", "case-set-differs"),
        "incomplete": count("class", "incomplete"),
        "not_judged": sum(1 for row in rows if row["class"] == "both" and not row["judged"]),
    }


def _short(name):
    return _BASE._short(name)


def _shown_ratios(row):
    """The ratios a verdict was taken on: to the floor for a sub-floor reference, else H/R."""
    return row.get("floor_ratios", row["ratios"])


def _shown_g(row):
    """The geometric mean a verdict was taken on: to the floor for a sub-floor reference."""
    return row.get("floor_geomean", row["geomean"])


def _num(value, spec):
    return "—" if value is None else format(value, spec)


def render_markdown(meta, summary, rows, structural, notices, enforce):
    order = " ".join(meta["order"])
    out = [f"## In-job comparison: HEAD against {meta['reference_tag']} ({order})", ""]
    mode = ("ENFORCING" if enforce else
            "OBSERVING (reports, cannot fail the job)")
    out += [f"Mode: **{mode}**, switch `{SWITCH}` in `.github/workflows/perf-regression.yml`. "
            f"Reference `{meta['reference_tag']}` at `{meta['reference_sha'] or 'unknown'}`, "
            f"HEAD at `{meta['head_sha'] or 'unknown'}`. {meta['launch_count']} launch(es) per "
            f"invocation.", ""]
    if meta["aa_run"]:
        out += ["No compiled file differs from the reference: this run measures the comparison's "
                "own noise.", ""]
    elif meta["changed_compiled_paths"]:
        shown = ", ".join(f"`{p}`" for p in meta["changed_compiled_paths"][:20])
        more = len(meta["changed_compiled_paths"]) - 20
        out += [f"Compiled files that differ from the reference: {shown}"
                f"{f' and {more} more' if more > 0 else ''}.", ""]
    for notice in notices:
        out += [f"> {notice}", ""]
    env = meta["environment"]
    out += [f"Measured on {', '.join(env.get('processor', [])) or 'an unrecorded processor'}; "
            f"SDK {', '.join(env.get('SDK', [])) or '?'}, runtime "
            f"{', '.join(env.get('runtime', [])) or '?'}, "
            f"{', '.join(env.get('logical core count', [])) or '?'} logical cores, JIT "
            f"{meta['thresholds']['jit_isa'] or '(not pinned)'}, BenchmarkDotNet "
            f"{', '.join(env.get('BenchmarkDotNet version', [])) or '?'}. Time bars: SLOWER "
            f"when the geometric mean g of the rounds is over "
            f"x{meta['thresholds']['slower_ratio']:g} and so is a majority of the rounds, FASTER "
            f"when g and a majority are under x{meta['thresholds']['faster_ratio']:g}, UNCONFIRMED "
            f"when only one of the two is; a round beyond a bar in an ok row is a lone round. A "
            f"case under {meta['thresholds']['min_mean_ns']:g} ns in HEAD is not judged, and one "
            f"under it in the reference only is judged SLOWER-only by the same rule, against "
            f"x{meta['thresholds']['sub_floor_slower_ratio']:g} the larger of the floor and the "
            f"reference.", ""]
    out += ["| position | arm | started | ended | exit | timed out |", "|--:|---|---|---|--:|---|"]
    for inv in meta["invocations"]:
        out.append(f"| {inv.get('position')} | {inv.get('arm')} | {inv.get('started') or '—'} | "
                   f"{inv.get('ended') or '—'} | {inv.get('exit_code')} | "
                   f"{'yes' if inv.get('timed_out') else 'no'} |")
    out.append("")

    def section(title, explanation, items):
        if items:
            out.extend([f"### {title} ({len(items)})", "", explanation, ""])
            out.extend(f"- {item}" for item in items)
            out.append("")

    def row_items(predicate, describe):
        return [describe(row) for row in rows if predicate(row)]

    section("Structural failures", "The comparison did not happen as declared.", structural)
    section("Failing", "SLOWER (g and a majority of the rounds over the bar), or MORE-BYTES in "
            "every round. These fail an enforcing run.",
            row_items(lambda r: r["verdict"] == "SLOWER",
                      lambda r: f"`{r['name']}` SLOWER: g {_shown_g(r):.4f}, "
                                f"{'ratios to the floor' if r.get('sub_floor') else 'ratios'} "
                                f"{', '.join(f'{x:.4f}' for x in _shown_ratios(r))}"
                                f", bar {r['thresholds']['slower']:.4f}")
            + row_items(lambda r: r["bytes_verdict"] == "MORE-BYTES",
                        lambda r: f"`{r['name']}` MORE-BYTES: deltas "
                                  f"{', '.join(f'{d:+}' for d in r['bytes_delta'])} B"))
    section("Reported, not failing", "FASTER (g and a majority of the rounds under the bar), or "
            "FEWER-BYTES in every round.",
            row_items(lambda r: r["verdict"] == "FASTER",
                      lambda r: f"`{r['name']}` FASTER: g {r['geomean']:.4f}, ratios "
                                f"{', '.join(f'{x:.4f}' for x in r['ratios'])}")
            + row_items(lambda r: r["bytes_verdict"] == "FEWER-BYTES",
                        lambda r: f"`{r['name']}` FEWER-BYTES: deltas "
                                  f"{', '.join(f'{d:+}' for d in r['bytes_delta'])} B"))
    section("Unconfirmed", "g beyond a bar without a majority of the rounds, or a majority "
            "beyond it without g. Reported, never failing.",
            row_items(lambda r: r["verdict"] == "UNCONFIRMED",
                      lambda r: f"`{r['name']}` g {_shown_g(r):.4f}, "
                                f"{'ratios to the floor' if r.get('sub_floor') else 'ratios'} "
                                f"{', '.join(f'{x:.4f}' for x in _shown_ratios(r))}"))
    section("Bytes anomalies", "An exactly gated case whose bytes differ between the rounds of "
            "one arm: allocation that is not deterministic.",
            row_items(lambda r: r.get("bytes_anomaly"),
                      lambda r: f"`{r['name']}` R {r['bytes']['R']}, H {r['bytes']['H']}"))
    section("Not compared: the benchmark itself differs", "The class's source or the job it "
            "ran under differs between the arms, so a ratio would include the benchmark's own "
            "change. Reported as a finding; a class with no judged row is structural.",
            row_items(lambda r: r.get("blind"),
                      lambda r: f"`{r['name']}`: {r['not_judged_reason']}"))
    section("HEAD only", "No reference: not compared.",
            row_items(lambda r: r["class"] == "head-only", lambda r: f"`{r['name']}`"))
    section("Reference only", "Removed or renamed since the reference tag.",
            row_items(lambda r: r["class"] == "reference-only", lambda r: f"`{r['name']}`"))
    section("Case set differs", "A parameter case one arm measures and the other does not.",
            row_items(lambda r: r["class"] == "case-set-differs",
                      lambda r: f"`{r['name']}` in arm {r['arm']} only"))
    section("Not judged", "Shown in the table, not judged.",
            row_items(lambda r: r["class"] in ("both", "incomplete") and not r["judged"]
                      and not r.get("blind"),
                      lambda r: f"`{r['name']}`: {r['not_judged_reason']}"))

    out += ["### Noise of this run", ""]
    if summary["judged_rows"]:
        out += [f"{summary['judged_rows']} judged rows, {summary['geomeans_count']} geometric "
                f"means: median |ln g| {summary['median_abs_log_geomean']:.4f}, p95 "
                f"{summary['p95_abs_log_geomean']:.4f}, "
                f"{summary['share_geomean_outside_band']:.1%} of g outside their bars. {summary['ratios_count']} ratios: median |ln r| "
                f"{summary['median_abs_log_ratio']:.4f}; median |ln self-ratio| "
                f"{summary['median_abs_log_self']:.4f}; order effect geomean(R last / R first) "
                f"{summary['order_effect_geomean']:.4f}; {summary['lone_rounds']} lone round(s), "
                f"absorbed by the majority.", ""]
    else:
        out += ["No row was judged.", ""]

    labels = [f"{inv['position']} {inv['arm']}" for inv in meta["invocations"]]
    out += ["### Measured", "",
            "| benchmark | " + " | ".join(f"{label} (ns)" for label in labels)
            + " | " + " | ".join(f"r{k}" for k in range(1, len(meta["order"]) // 2 + 1))
            + " | geomean | R self | H self | bytes R / H | ΔB per round | verdict |",
            "|---|" + "--:|" * (len(labels) + len(meta["order"]) // 2 + 3) + "---|---|---|"]
    for row in rows:
        per_position = []
        counters = {"R": 0, "H": 0}
        for inv in meta["invocations"]:
            arm = inv["arm"]
            values = row["means"][arm]
            per_position.append(_num(values[counters[arm]] if counters[arm] < len(values)
                                     else None, ".2f"))
            counters[arm] += 1
        ratios = [_num(r, ".4f") for r in row["ratios"]] or ["—"] * (len(meta["order"]) // 2)
        rb = sorted({b for b in row["bytes"]["R"] if b is not None})
        hb = sorted({b for b in row["bytes"]["H"] if b is not None})
        bytes_text = f"{'/'.join(map(str, rb)) or '—'} / {'/'.join(map(str, hb)) or '—'}"
        delta = ", ".join(f"{d:+}" for d in row["bytes_delta"]) or "—"
        verdict = row["verdict"] if row["class"] == "both" else row["class"]
        if row["bytes_judged"] and row["bytes_verdict"] != "ok":
            verdict += f" {row['bytes_verdict']}"
        out.append(f"| `{_short(row['name'])}` | " + " | ".join(per_position) + " | "
                   + " | ".join(ratios) + f" | {_num(row['geomean'], '.4f')} | "
                   f"{_num(row['self']['R'], '.4f')} | {_num(row['self']['H'], '.4f')} | "
                   f"{bytes_text} | {delta} | {verdict} |")
    out.append("")
    return "\n".join(out)


def main(argv):
    baseline_path, root, markdown_out, json_out, enforce = parse_args(argv)
    sources, baseline, _declared, jit_isa, environment = _BASE.load_baseline(baseline_path)
    types = set(sources.values())
    abab = load_abab(baseline_path, types)
    manifest = load_manifest(root, abab)

    structural = []
    lists = {}
    for arm in ("R", "H"):
        lists[arm], problem = read_list(root, arm, types)
        if problem:
            structural.append(problem)
    # HEAD's sources are HEAD's declaration: a declared type HEAD's own list does not name was
    # renamed or lost its filter, and nothing of it would be compared. The reference may lack a
    # type (it is new since the tag): its cases are then HEAD only, never structural.
    if lists["H"]:
        listed = {m.rsplit(".", 1)[0] for m in lists["H"]}
        for type_name in sorted(types - listed):
            structural.append(f"[{type_name}] is a baseline.json \"sources\" type that "
                              f"list-H.txt does not name: HEAD declares it and never ran it.")
    if manifest.get("reference_dir_sha") is None or manifest.get("reference_sha") is None \
            or manifest.get("reference_dir_sha") != manifest.get("reference_sha"):
        structural.append(f"the reference directory is at commit "
                          f"{manifest.get('reference_dir_sha')}, not the tag's commit "
                          f"{manifest.get('reference_sha')}: the R arm is not the declared "
                          f"reference.")
    invocations = {}
    positions = {"R": [], "H": []}
    for inv in manifest["invocations"]:
        reports, problems = read_invocation(root, inv, sources, environment, jit_isa,
                                            abab["launch_count"])
        invocations[inv["dir"]] = reports
        positions[inv["arm"]].append(inv["dir"])
        structural += problems
    host_problems, observed_env = check_hosts(
        [(label, invocations[label]) for label in sorted(invocations)], environment)
    structural += host_problems
    rows, more = compare(abab, baseline, manifest, lists, invocations, positions)
    structural += more
    summary = summarize(rows, structural)

    notices = []
    newest = manifest.get("newest_tag")
    stale = isinstance(newest, str) and newest != abab["reference_tag"]
    if stale:
        notices.append(f"The newest release tag is {newest}; this run still compares against "
                       f"{abab['reference_tag']}. Move baseline.json `abab.reference_tag` by PR "
                       f"(baseline.README.md).")
    changed = manifest.get("changed_benchmark_paths") or []
    source_files = {f"{BENCHMARK_DIR}{t.rsplit('.', 1)[-1]}.cs" for t in types}
    project = sorted(p for p in changed if p.startswith(BENCHMARK_DIR)
                     and p[len(BENCHMARK_DIR):] not in PROJECT_EXEMPT and p not in source_files)
    if project:
        notices.append("The benchmark project differs from the reference outside the compared "
                       "sources: " + ", ".join(f"`{p}`" for p in project)
                       + ". Rows stay judged; read them knowing the harness changed.")
    for row in rows:
        if row["class"] in ("head-only", "reference-only", "case-set-differs"):
            notices.append(f"`{row['name']}` is {row['class']}: {row['not_judged_reason']}.")

    compiled = manifest.get("changed_compiled_paths")
    meta = {
        "schema": 1,
        "mode": "ENFORCING" if enforce else "OBSERVING",
        "enforcing": enforce,
        "switch": SWITCH,
        "reference_tag": abab["reference_tag"],
        "reference_sha": manifest.get("reference_sha"),
        "head_sha": manifest.get("head_sha"),
        "newest_tag": newest,
        "stale_reference": stale,
        "order": abab["order"],
        "launch_count": abab["launch_count"],
        "aa_run": (compiled == []) if compiled is not None else None,
        "changed_compiled_paths": compiled,
        "changed_benchmark_paths": manifest.get("changed_benchmark_paths"),
        "environment": observed_env,
        "invocations": manifest["invocations"],
        "thresholds": {"slower_ratio": abab["slower_ratio"], "faster_ratio": abab["faster_ratio"],
                       "min_mean_ns": abab["min_mean_ns"],
                       "sub_floor_slower_ratio": abab["sub_floor_slower_ratio"],
                       "jit_isa": jit_isa},
    }

    print(f"perf-abab: mode={meta['mode']} reference={abab['reference_tag']} "
          f"order={' '.join(abab['order'])} launch_count={abab['launch_count']} root={root}")
    for row in rows:
        ratios = ",".join(f"{r:.4f}" for r in row["ratios"]) or "-"
        g = "-" if row["geomean"] is None else f"{row['geomean']:.4f}"
        print(f"{_short(row['name']):<70} {row['class']:<16} ratios={ratios:<23} g={g:<6} "
              f"verdict={row['verdict']} bytes={row['bytes_verdict']}")
    level = "error" if enforce else "warning"
    for item in structural:
        print(f"::{level}::perf-abab STRUCTURAL: {item}")
    for row in rows:
        if row["verdict"] == "SLOWER":
            print(f"::{level}::perf-abab SLOWER: {row['name']} g {_shown_g(row):.4f} and a "
                  f"majority of the ratios {', '.join(f'{r:.4f}' for r in _shown_ratios(row))} "
                  f"over {row['thresholds']['slower']:.4f}.")
        if row["bytes_verdict"] == "MORE-BYTES":
            print(f"::{level}::perf-abab MORE-BYTES: {row['name']} deltas {row['bytes_delta']} B.")
        if row["verdict"] == "FASTER" or row["bytes_verdict"] == "FEWER-BYTES":
            print(f"::warning::perf-abab {row['verdict']}/{row['bytes_verdict']}: {row['name']}. "
                  f"Reported, not failing.")
    for notice in notices:
        print(f"::notice::perf-abab: {notice}")

    def strip(row):
        return {k: v for k, v in row.items() if k not in ("bytes_anomaly", "sub_floor",
                                                          "floor_ratios", "floor_geomean")}

    if markdown_out:
        try:
            with open(markdown_out, "w", encoding="utf-8") as handle:
                handle.write(render_markdown(meta, summary, rows, structural, notices, enforce))
        except OSError as exc:
            die(f"cannot write markdown report '{markdown_out}': {exc}")
    if json_out:
        try:
            with open(json_out, "w", encoding="utf-8") as handle:
                json.dump({"meta": meta, "summary": summary, "rows": [strip(r) for r in rows],
                           "structural": structural, "notices": notices}, handle, indent=1)
        except OSError as exc:
            die(f"cannot write JSON report '{json_out}': {exc}")

    failing = summary["failing"]
    findings = failing + summary["faster"] + summary["fewer_bytes"] + summary["unconfirmed"] \
        + summary["bytes_anomalies"] + summary["blind"]
    status = "pass" if findings == 0 else "breach"
    github_output = os.environ.get("GITHUB_OUTPUT")
    if github_output:
        try:
            with open(github_output, "a", encoding="utf-8") as handle:
                for key, value in (("status", status), ("slower", summary["slower"]),
                                   ("more_bytes", summary["more_bytes"]),
                                   ("faster", summary["faster"]),
                                   ("fewer_bytes", summary["fewer_bytes"]),
                                   ("unconfirmed", summary["unconfirmed"]),
                                   ("bytes_anomalies", summary["bytes_anomalies"]),
                                   ("blind", summary["blind"]),
                                   ("head_only", summary["head_only"]),
                                   ("reference_only", summary["reference_only"]),
                                   ("structural", summary["structural"]),
                                   ("failing", failing),
                                   ("enforcing", "true" if enforce else "false")):
                    handle.write(f"abab_{key}={value}\n")
        except OSError as exc:
            die(f"cannot write GITHUB_OUTPUT '{github_output}': {exc}")

    print(f"perf-abab: {summary['judged_rows']} judged row(s); {summary['slower']} SLOWER, "
          f"{summary['more_bytes']} MORE-BYTES, {summary['faster']} FASTER, "
          f"{summary['fewer_bytes']} FEWER-BYTES, {summary['unconfirmed']} UNCONFIRMED, "
          f"{summary['lone_rounds']} lone round(s), {len(structural)} structural.")
    if failing and enforce:
        return 1
    if failing:
        print(f"perf-abab: OBSERVING — not failing the job. {SWITCH}='true' in "
              f".github/workflows/perf-regression.yml makes these fail it.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
