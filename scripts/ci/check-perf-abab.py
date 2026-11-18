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

THE ORDER AND THE ROUNDS. The declared order (`abab.order`, R H H R) is a palindrome: round k
pairs the k-th R invocation with the k-th H invocation, so round 1 = (position 1, position 2) and
round 2 = (position 4, position 3). Under a linear drift across the run the two rounds carry it
with opposite signs, so drift alone cannot make every round agree.

THE STATISTIC. For each case and round k, r_k = mean(H_k) / mean(R_k) (BenchmarkDotNet
`Statistics.Mean`, nanoseconds). SLOWER when every r_k > slower_ratio x accepted; FASTER when every
r_k < faster_ratio x accepted; UNCONFIRMED when some but not all rounds are beyond a bar; ok
otherwise. `accepted` is 1 unless `abab.accepted` records a cost measured against this same
reference. The point estimate shown is the geometric mean of the r_k. A case whose mean is under
`min_mean_ns` in any invocation is shown and not judged: a ratio of a few cycles measures
BenchmarkDotNet's overhead subtraction, not the code.

BYTES IN THE JOB. Both arms run on the same machine, so on the same core count, and a bytes
difference between them is the code. For a case with a `bytes` entry in baseline.json `benchmarks`,
MORE-BYTES when every round's delta exceeds its `bytes_tolerance`, FEWER-BYTES when every round's
delta is under minus it. A case with no `bytes` entry shows its delta and is not judged. An exactly
gated case whose bytes differ between the rounds of one arm is a bytes anomaly (reported).

NEVER DROPPED. The two `--list flat` outputs are the methods each tree declares. A method in one
list only is listed as HEAD only or reference only; a parameter case present in one arm only is
listed as a case-set difference. A case its arm declares but some invocation did not measure, a
declared class with no report, a mean that is not a finite number, an invocation that timed out, or
invocations that disagree on SDK, runtime, core count, JIT ISA, BenchmarkDotNet version or
processor are STRUCTURAL failures: the comparison did not happen as declared.

OBSERVING OR ENFORCING. `PERF_ABAB_ENFORCE` (or --enforce) selects the mode, and nothing else
does: `PERF_GATE_ENFORCE`, the absolute gate's switch, is not read here. Observing, every finding is
reported and the exit is 0. Enforcing, SLOWER, MORE-BYTES and structural failures exit 1.

Exit codes:
  0  pass; or only FASTER / FEWER-BYTES / UNCONFIRMED / not-judged / one-side-only findings; or
     observing with findings of any kind
  1  enforcing and at least one SLOWER, MORE-BYTES or structural failure
  2  the guard is misconfigured, in both modes: bad usage, a baseline.json the absolute
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
              "invocation_timeout_s", "rows", "accepted"}
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
    enforce = os.environ.get(SWITCH, "").strip().lower() in ("1", "true", "yes", "on")
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
    timeout = block["invocation_timeout_s"]
    if not isinstance(timeout, int) or isinstance(timeout, bool) or timeout <= 0:
        die(f"baseline \"abab.invocation_timeout_s\" must be a positive integer, not {timeout!r}.")

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
            "faster_ratio": faster, "min_mean_ns": float(floor), "invocation_timeout_s": timeout,
            "rows": overrides, "accepted": accepted}


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


def read_invocation(root, inv, sources, environment, jit_isa):
    """Every declared type's report in one invocation directory. Returns (reports, structural):
    reports maps type -> {"cases": {FullName: entry}, "host": HostEnvironmentInfo}."""
    label = inv["dir"]
    directory = os.path.join(root, label)
    structural = []
    reports = {}
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
        reports[type_name] = {"cases": cases, "host": doc.get("HostEnvironmentInfo")}
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
    declared = {}
    if environment:
        declared = {"SDK": environment["dotnet_sdk"], "runtime": environment["dotnet_runtime"],
                    "logical core count": environment["logical_cores"]}
    for what in ("SDK", "runtime", "logical core count", "BenchmarkDotNet version", "processor"):
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
               "thresholds": None, "verdict": "not judged"}
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
            _judge(row, abab, baseline, changed_sources, structural)
        rows.append(row)
    return rows, structural


def _int_or(value):
    return int(value) if float(value).is_integer() else value


def _judge(row, abab, baseline, changed_sources, structural):
    name = row["name"]
    rmeans, hmeans = row["means"]["R"], row["means"]["H"]
    override = abab["rows"].get(name, {})
    accepted = abab["accepted"].get(name, 1.0)
    slower = override.get("slower_ratio", abab["slower_ratio"]) * accepted
    faster = override.get("faster_ratio", abab["faster_ratio"]) * accepted
    row["thresholds"] = {"slower": slower, "faster": faster, "accepted": accepted}
    positive = all(m > 0 for m in rmeans + hmeans)
    if positive:
        row["ratios"] = [h / r for r, h in zip(rmeans, hmeans)]
        row["geomean"] = _geomean(row["ratios"])
        row["self"] = {"R": rmeans[-1] / rmeans[0], "H": hmeans[-1] / hmeans[0]}
    short = _BASE._owning_type(name).rsplit(".", 1)[-1]
    source_changed = short in changed_sources
    reason = None
    if source_changed:
        reason = ("the benchmark source differs from the reference: the ratio includes the "
                  "benchmark's own change")
    elif min(rmeans + hmeans) < abab["min_mean_ns"] or not positive:
        reason = (f"a mean is under the {abab['min_mean_ns']:g} ns floor: the ratio would measure "
                  f"BenchmarkDotNet's overhead subtraction")
    elif override.get("judge_time") is False:
        reason = f"time not judged by abab.rows: {override['reason']}"
    if reason:
        row["not_judged_reason"] = reason
    else:
        row["judged"] = True
        above = [r > slower for r in row["ratios"]]
        below = [r < faster for r in row["ratios"]]
        if all(above):
            row["verdict"] = "SLOWER"
        elif all(below):
            row["verdict"] = "FASTER"
        elif any(above) or any(below):
            row["verdict"] = "UNCONFIRMED"
        else:
            row["verdict"] = "ok"

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
    if source_changed:
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
    judged = [row for row in rows if row["judged"]]
    logs = [abs(math.log(r)) for row in judged for r in row["ratios"]]
    selfs = [abs(math.log(row["self"][arm])) for row in judged for arm in ("R", "H")]
    outside = sum(1 for row in judged for r in row["ratios"]
                  if r > row["thresholds"]["slower"] or r < row["thresholds"]["faster"])
    count = lambda key, value: sum(1 for row in rows if row[key] == value)  # noqa: E731
    slower, more = count("verdict", "SLOWER"), count("bytes_verdict", "MORE-BYTES")
    return {
        "judged_rows": len(judged),
        "ratios_count": len(logs),
        "median_abs_log_ratio": _median(logs),
        "p95_abs_log_ratio": _p95(logs),
        "median_abs_log_self": _median(selfs),
        "order_effect_geomean": _geomean([row["self"]["R"] for row in judged]) if judged else None,
        "share_outside_band": outside / len(logs) if logs else None,
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


def _num(value, spec):
    return "—" if value is None else format(value, spec)


def render_markdown(meta, summary, rows, structural, notices, enforce):
    order = " ".join(meta["order"])
    out = [f"## In-job comparison: HEAD against {meta['reference_tag']} ({order})", ""]
    mode = ("ENFORCING" if enforce else
            "OBSERVING (reports, cannot fail the job)")
    out += [f"Mode: **{mode}**, switch `{SWITCH}` in `.github/workflows/perf-regression.yml`. "
            f"Reference `{meta['reference_tag']}` at `{meta['reference_sha'] or 'unknown'}`, "
            f"HEAD at `{meta['head_sha'] or 'unknown'}`.", ""]
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
            f"when every round is over x{meta['thresholds']['slower_ratio']:g}, FASTER when every "
            f"round is under x{meta['thresholds']['faster_ratio']:g}; means under "
            f"{meta['thresholds']['min_mean_ns']:g} ns are not judged.", ""]
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
    section("Failing", "SLOWER in every round, or MORE-BYTES in every round. These fail an "
            "enforcing run.",
            row_items(lambda r: r["verdict"] == "SLOWER",
                      lambda r: f"`{r['name']}` SLOWER: ratios "
                                f"{', '.join(f'{x:.4f}' for x in r['ratios'])}, bar "
                                f"{r['thresholds']['slower']:.4f}")
            + row_items(lambda r: r["bytes_verdict"] == "MORE-BYTES",
                        lambda r: f"`{r['name']}` MORE-BYTES: deltas "
                                  f"{', '.join(f'{d:+}' for d in r['bytes_delta'])} B"))
    section("Reported, not failing", "FASTER or FEWER-BYTES in every round.",
            row_items(lambda r: r["verdict"] == "FASTER",
                      lambda r: f"`{r['name']}` FASTER: ratios "
                                f"{', '.join(f'{x:.4f}' for x in r['ratios'])}")
            + row_items(lambda r: r["bytes_verdict"] == "FEWER-BYTES",
                        lambda r: f"`{r['name']}` FEWER-BYTES: deltas "
                                  f"{', '.join(f'{d:+}' for d in r['bytes_delta'])} B"))
    section("Unconfirmed (one round only)", "Beyond a bar in some rounds and not in others. "
            "Reported, never failing.",
            row_items(lambda r: r["verdict"] == "UNCONFIRMED",
                      lambda r: f"`{r['name']}` ratios "
                                f"{', '.join(f'{x:.4f}' for x in r['ratios'])}"))
    section("Bytes anomalies", "An exactly gated case whose bytes differ between the rounds of "
            "one arm: allocation that is not deterministic.",
            row_items(lambda r: r.get("bytes_anomaly"),
                      lambda r: f"`{r['name']}` R {r['bytes']['R']}, H {r['bytes']['H']}"))
    section("HEAD only", "No reference: not compared.",
            row_items(lambda r: r["class"] == "head-only", lambda r: f"`{r['name']}`"))
    section("Reference only", "Removed or renamed since the reference tag.",
            row_items(lambda r: r["class"] == "reference-only", lambda r: f"`{r['name']}`"))
    section("Case set differs", "A parameter case one arm measures and the other does not.",
            row_items(lambda r: r["class"] == "case-set-differs",
                      lambda r: f"`{r['name']}` in arm {r['arm']} only"))
    section("Not judged", "Shown in the table, not judged.",
            row_items(lambda r: r["class"] in ("both", "incomplete") and not r["judged"],
                      lambda r: f"`{r['name']}`: {r['not_judged_reason']}"))

    out += ["### Noise of this run", ""]
    if summary["judged_rows"]:
        out += [f"{summary['judged_rows']} judged rows, {summary['ratios_count']} ratios: median "
                f"|ln r| {summary['median_abs_log_ratio']:.4f}, p95 "
                f"{summary['p95_abs_log_ratio']:.4f}; median |ln self-ratio| "
                f"{summary['median_abs_log_self']:.4f}; order effect geomean(R last / R first) "
                f"{summary['order_effect_geomean']:.4f}; {summary['share_outside_band']:.1%} of "
                f"ratios outside their bars.", ""]
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
    if manifest.get("reference_dir_sha") != manifest.get("reference_sha"):
        structural.append(f"the reference directory is at commit "
                          f"{manifest.get('reference_dir_sha')}, not the tag's commit "
                          f"{manifest.get('reference_sha')}: the R arm is not the declared "
                          f"reference.")
    invocations = {}
    positions = {"R": [], "H": []}
    for inv in manifest["invocations"]:
        reports, problems = read_invocation(root, inv, sources, environment, jit_isa)
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
        "aa_run": (compiled == []) if compiled is not None else None,
        "changed_compiled_paths": compiled,
        "changed_benchmark_paths": manifest.get("changed_benchmark_paths"),
        "environment": observed_env,
        "invocations": manifest["invocations"],
        "thresholds": {"slower_ratio": abab["slower_ratio"], "faster_ratio": abab["faster_ratio"],
                       "min_mean_ns": abab["min_mean_ns"], "jit_isa": jit_isa},
    }

    print(f"perf-abab: mode={meta['mode']} reference={abab['reference_tag']} "
          f"order={' '.join(abab['order'])} root={root}")
    for row in rows:
        ratios = ",".join(f"{r:.4f}" for r in row["ratios"]) or "-"
        print(f"{_short(row['name']):<70} {row['class']:<16} ratios={ratios:<16} "
              f"verdict={row['verdict']} bytes={row['bytes_verdict']}")
    level = "error" if enforce else "warning"
    for item in structural:
        print(f"::{level}::perf-abab STRUCTURAL: {item}")
    for row in rows:
        if row["verdict"] == "SLOWER":
            print(f"::{level}::perf-abab SLOWER: {row['name']} ratios "
                  f"{', '.join(f'{r:.4f}' for r in row['ratios'])} over "
                  f"{row['thresholds']['slower']:.4f} in every round.")
        if row["bytes_verdict"] == "MORE-BYTES":
            print(f"::{level}::perf-abab MORE-BYTES: {row['name']} deltas {row['bytes_delta']} B.")
        if row["verdict"] == "FASTER" or row["bytes_verdict"] == "FEWER-BYTES":
            print(f"::warning::perf-abab {row['verdict']}/{row['bytes_verdict']}: {row['name']}. "
                  f"Reported, not failing.")
    for notice in notices:
        print(f"::notice::perf-abab: {notice}")

    def strip(row):
        return {k: v for k, v in row.items() if k != "bytes_anomaly"}

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
        + summary["bytes_anomalies"]
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
          f"{len(structural)} structural.")
    if failing and enforce:
        return 1
    if failing:
        print(f"perf-abab: OBSERVING — not failing the job. {SWITCH}='true' in "
              f".github/workflows/perf-regression.yml makes these fail it.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
