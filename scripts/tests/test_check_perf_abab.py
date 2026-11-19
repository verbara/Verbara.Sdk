"""Unit tests for scripts/ci/check-perf-abab.py (the in-job comparison against a reference tag).

The weekly perf job builds the declared reference tag beside HEAD and measures both in the same
job, in the declared order (R H H R R H), and this comparator judges time by the geometric mean of
the per-round ratios HEAD/reference, confirmed by a majority of the rounds. It runs only in a
scheduled job, so like the absolute gate it needs its own guard here. Most tests run the two-round
order R H H R, which keeps today's verdicts (a regression control); section 31 runs three.

The tests run the comparator as a subprocess on synthetic `abab-root` trees: one directory per
invocation (`<position>-<arm>/results/<Type>-report-full-compressed.json` plus a BenchmarkDotNet
log), the two `--list flat` outputs and the run script's manifest.

Also pins the contracts this comparison shares with the committed baseline.json and with
.github/workflows/perf-regression.yml: the `abab` block loads, the workflow declares the switch
once and observes, and the run fits its timeouts.

Stdlib unittest only, no pip deps. Discovered by `python3 -m unittest discover scripts/tests`.
"""
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_HERE, os.pardir, os.pardir))
_SCRIPT = os.path.join(_REPO, "scripts", "ci", "check-perf-abab.py")
_ABSOLUTE = os.path.join(_REPO, "scripts", "ci", "check-perf-baseline.py")
_REAL_BASELINE = os.path.join(_REPO, "Tests", "Verbara.Sdk.Benchmarks", "baseline.json")
_WORKFLOW = os.path.join(_REPO, ".github", "workflows", "perf-regression.yml")

_TYPE = "Verbara.Sdk.Benchmarks.FakeBenchmark"
_ALPHA = f"{_TYPE}.Alpha"     # time 1000 ns, bytes gated exactly (168 B)
_BETA = f"{_TYPE}.Beta"       # time only, no bytes entry
_GAMMA = f"{_TYPE}.Gamma"     # bytes gated within a 1024 B bound
_TINY = f"{_TYPE}.Tiny"       # 1 ns: below the floor, declared unbanded

_JOB = "ShortRun(IterationCount=3, LaunchCount=1, WarmupCount=3)"
_INTRINSICS = "AVX2+BMI1+BMI2+F16C+FMA+LZCNT+MOVBE,AVX,SSE3+SSSE3+SSE4.1+SSE4.2+POPCNT VectorSize=256"

_ENVIRONMENT = {"dotnet_sdk": "10.0.404", "dotnet_runtime": "10.0.15", "logical_cores": 4}

_ABAB = {
    "reference_tag": "v2.10.1",
    "order": ["R", "H", "H", "R"],
    "slower_ratio": 1.10,
    "faster_ratio": 0.909,
    "min_mean_ns": 2.0,
    "sub_floor_slower_ratio": 2.0,
    "invocation_timeout_s": 900,
    "launch_count": 1,
    "rows": {},
    "accepted": {},
}

_BASELINE = {
    "sources": {"fake": _TYPE},
    "jit_isa": "x86-64-v3",
    "environment": _ENVIRONMENT,
    "benchmarks": {
        _ALPHA: {"mean_ns": 1000.0, "tolerance_pct": 10, "bytes": 168, "bytes_tolerance": 0},
        _BETA: {"mean_ns": 50.0, "tolerance_pct": 50},
        _GAMMA: {"bytes": 500000, "bytes_tolerance": 1024},
    },
    "unbanded": {_TINY: {"reason": "resolution floor"}},
    "abab": _ABAB,
}

_DEFAULTS = {_ALPHA: (1000.0, 168), _BETA: (50.0, 0), _GAMMA: (2000.0, 500000), _TINY: (1.0, 0)}

_ROW_KEYS = {"name", "class", "arm", "judged", "not_judged_reason", "means", "ratios", "geomean",
             "self", "bytes", "bytes_delta", "bytes_judged", "bytes_verdict", "thresholds",
             "verdict", "blind", "lone_rounds"}
_META_KEYS = {"schema", "mode", "enforcing", "switch", "reference_tag", "reference_sha",
              "head_sha", "newest_tag", "stale_reference", "order", "aa_run",
              "changed_compiled_paths", "changed_benchmark_paths", "environment", "invocations",
              "thresholds", "launch_count"}
_SUMMARY_KEYS = {"judged_rows", "ratios_count", "median_abs_log_ratio", "geomeans_count",
                 "median_abs_log_geomean", "p95_abs_log_geomean", "share_geomean_outside_band",
                 "lone_rounds", "median_abs_log_self", "order_effect_geomean", "slower",
                 "faster", "unconfirmed", "more_bytes", "fewer_bytes", "bytes_anomalies",
                 "structural", "failing", "head_only", "reference_only", "case_set_differs",
                 "incomplete", "not_judged", "sub_floor_judged", "blind"}


_FOLLOW = object()  # the manifest's launch_count follows the baseline's


def _method(full_name):
    return re.sub(r"\([^()]*\)$", "", full_name)


def _deep(doc):
    return json.loads(json.dumps(doc))


def _results_of(job):
    """What BenchmarkDotNet records for a job text: IterationCount Result iterations in each of
    LaunchCount launches (1 when the text names none)."""
    launches = re.search(r"LaunchCount=(\d+)", job)
    iterations = re.search(r"IterationCount=(\d+)", job)
    count = int(iterations.group(1)) if iterations else 3
    return {launch: count for launch in range(1, (int(launches.group(1)) if launches else 1) + 1)}


def _measurements(results, mean):
    """BenchmarkDotNet 0.15.8's `Measurements` shape: per launch, warmup and actual iterations
    and the Result iterations the statistics are computed from."""
    out = []
    ops = 1000
    value = mean if isinstance(mean, (int, float)) else 1.0
    for launch, count in sorted(results.items()):
        for stage, n in (("Warmup", 3), ("Actual", count), ("Result", count)):
            out.extend({"IterationMode": "Workload", "IterationStage": stage,
                        "LaunchIndex": launch, "IterationIndex": i, "Operations": ops,
                        "Nanoseconds": value * ops} for i in range(1, n + 1))
    return out


class CheckPerfAbabTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.mkdtemp()
        self.baseline_path = os.path.join(self._tmp, "baseline.json")
        self.baseline = _deep(_BASELINE)
        self.root = os.path.join(self._tmp, "artifacts-abab")
        self.markdown = os.path.join(self._tmp, "report.md")
        self.json_out = os.path.join(self._tmp, "perf-abab.json")
        self.gh_output = os.path.join(self._tmp, "gh_output.txt")
        self.order = list(_ABAB["order"])
        # position -> case -> [mean, bytes]
        self.values = {pos: {name: list(value) for name, value in _DEFAULTS.items()}
                       for pos in range(1, len(self.order) + 1)}
        self.lists = {"R": sorted({_method(n) for n in _DEFAULTS}),
                      "H": sorted({_method(n) for n in _DEFAULTS})}
        self.host = {pos: {} for pos in self.values}
        self.isa = {pos: "x86-64-v3" for pos in self.values}
        self.no_report = set()
        self.raw_stats = {}  # (pos, case) -> raw Statistics object
        self.job = {}        # (pos, case) -> DisplayInfo job text (None: no DisplayInfo)
        self.intrinsics = {}  # (pos, case) -> HardwareIntrinsics (None: absent)
        # (pos, case) -> Result iterations per LaunchIndex, e.g. {1: 3, 2: 3}; None: no
        # Measurements. Default: the job text's LaunchCount and IterationCount.
        self.results = {}
        self.manifest = {
            "schema": 1,
            "reference_tag": "v2.10.1",
            "reference_sha": "a" * 40,
            "reference_dir_sha": "a" * 40,
            "head_sha": "b" * 40,
            "newest_tag": "v2.10.1",
            "order": list(self.order),
            "invocations": [],
            "lists": {"R": {"exit_code": 0}, "H": {"exit_code": 0}},
            "changed_benchmark_paths": [],
            "changed_compiled_paths": [],
        }
        self.write_manifest = True
        self.manifest_launch_count = _FOLLOW

    def tearDown(self):
        shutil.rmtree(self._tmp, ignore_errors=True)

    # ---- fixture -------------------------------------------------------------------------
    def set(self, pos, case, mean=None, allocated=None):
        current = self.values[pos].setdefault(case, [1000.0, 0])
        if mean is not None:
            current[0] = mean
        if allocated is not None:
            current[1] = allocated

    def set_arm(self, arm, case, mean=None, allocated=None):
        for pos, which in enumerate(self.order, start=1):
            if which == arm:
                self.set(pos, case, mean, allocated)

    def _materialize(self):
        with open(self.baseline_path, "w", encoding="utf-8") as handle:
            json.dump(self.baseline, handle)
        os.makedirs(self.root, exist_ok=True)
        for arm, methods in self.lists.items():
            if methods is None:
                continue
            with open(os.path.join(self.root, f"list-{arm}.txt"), "w", encoding="utf-8") as handle:
                handle.write("".join(f"{m}\n" for m in methods))
        invocations = []
        for pos, arm in enumerate(self.order, start=1):
            directory = os.path.join(self.root, f"{pos}-{arm}")
            invocations.append({"position": pos, "arm": arm, "dir": f"{pos}-{arm}",
                                "started": "2026-11-17T04:00:00Z",
                                "ended": "2026-11-17T04:06:00Z", "exit_code": 0,
                                "timed_out": False})
            if pos in self.no_report:
                continue
            os.makedirs(os.path.join(directory, "results"), exist_ok=True)
            if self.isa.get(pos):
                with open(os.path.join(directory, "BenchmarkRun-20261117-040000.log"), "w",
                          encoding="utf-8") as handle:
                    handle.write(f"// Runtime=.NET 10.0.15 (10.0.15, 10.0.1526.53008), X64 "
                                 f"RyuJIT {self.isa[pos]}\n")
            entries = []
            for name, (mean, allocated) in sorted(self.values.get(pos, {}).items()):
                method = _method(name).rsplit(".", 1)[1]
                entry = {"Namespace": "Verbara.Sdk.Benchmarks", "Type": "FakeBenchmark",
                         "Method": method, "FullName": name,
                         "Statistics": {"N": 3, "Mean": mean, "Median": mean}}
                launches = self.baseline.get("abab", {}).get("launch_count", 1)
                default_job = _JOB.replace("LaunchCount=1", f"LaunchCount={launches}")
                job = self.job.get((pos, name), default_job)
                if job is not None:
                    entry["DisplayInfo"] = f"FakeBenchmark.{method}: {job}"
                results = self.results.get((pos, name), _results_of(job or default_job))
                if results is not None:
                    entry["Measurements"] = _measurements(results, mean)
                intrinsics = self.intrinsics.get((pos, name), _INTRINSICS)
                if intrinsics is not None:
                    entry["HardwareIntrinsics"] = intrinsics
                if (pos, name) in self.raw_stats:
                    entry["Statistics"] = self.raw_stats[(pos, name)]
                if allocated is not None:
                    entry["Memory"] = {"BytesAllocatedPerOperation": allocated}
                entries.append(entry)
            host = {"BenchmarkDotNetVersion": "0.15.8", "ProcessorName": "AMD EPYC 7763",
                    "LogicalCoreCount": 4, "DotNetCliVersion": "10.0.404",
                    "RuntimeVersion": ".NET 10.0.15 (10.0.15, 10.0.1526.53008)"}
            host.update(self.host[pos])
            with open(os.path.join(directory, "results",
                                   f"{_TYPE}-report-full-compressed.json"), "w",
                      encoding="utf-8") as handle:
                handle.write(json.dumps({"Title": "FakeBenchmark", "HostEnvironmentInfo": host,
                                         "Benchmarks": entries}))
        if not self.manifest.get("invocations"):
            self.manifest["invocations"] = invocations
        # The run script writes the launch count it read; a test overrides it to break that.
        self.manifest["launch_count"] = (
            self.baseline.get("abab", {}).get("launch_count")
            if self.manifest_launch_count is _FOLLOW else self.manifest_launch_count)
        if self.write_manifest:
            with open(os.path.join(self.root, "manifest.json"), "w", encoding="utf-8") as handle:
                json.dump(self.manifest, handle)

    def _run(self, *extra, enforce=False, env=None, materialize=True):
        if materialize:
            self._materialize()
        cmd = [sys.executable, _SCRIPT, "--baseline", self.baseline_path,
               "--abab-root", self.root, "--markdown-out", self.markdown,
               "--json-out", self.json_out] + list(extra)
        environ = dict(os.environ, GITHUB_OUTPUT=self.gh_output)
        environ.pop("PERF_ABAB_ENFORCE", None)
        environ.pop("PERF_GATE_ENFORCE", None)
        if enforce:
            environ["PERF_ABAB_ENFORCE"] = "true"
        if env:
            environ.update(env)
        return subprocess.run(cmd, capture_output=True, text=True, check=False, env=environ)

    def _json(self):
        with open(self.json_out, encoding="utf-8") as handle:
            return json.load(handle)

    def _row(self, name):
        rows = [row for row in self._json()["rows"] if row["name"] == name]
        self.assertEqual(1, len(rows), f"expected one row for {name}")
        return rows[0]

    def _md(self):
        with open(self.markdown, encoding="utf-8") as handle:
            return handle.read()

    def _outputs(self):
        with open(self.gh_output, encoding="utf-8") as handle:
            return dict(line.strip().split("=", 1) for line in handle if "=" in line)

    def _structural(self):
        return self._json()["structural"]

    # ---- 1. the happy path ---------------------------------------------------------------
    def test_Main_ShouldPass_WhenBothArmsMeasureTheSame(self):
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        doc = self._json()
        self.assertEqual([], doc["structural"])
        for row in doc["rows"]:
            expected = "not judged" if row["name"] == _TINY else "ok"
            self.assertEqual(expected, row["verdict"], row)
            self.assertIn(row["bytes_verdict"], ("ok", "not judged"), row)
        self.assertEqual(3, doc["summary"]["judged_rows"])
        self.assertEqual("pass", self._outputs()["abab_status"])

    # ---- 2-7. the time verdict -----------------------------------------------------------
    def test_Main_ShouldReportSlower_WhenEveryRoundExceedsTheRatio(self):
        self.set_arm("H", _ALPHA, mean=1200.0)
        result = self._run()
        self.assertEqual(0, result.returncode, result.stdout)
        row = self._row(_ALPHA)
        self.assertEqual("SLOWER", row["verdict"])
        self.assertAlmostEqual(1.2, row["geomean"], places=6)
        self.assertIn("SLOWER", self._md())

    def test_Main_ShouldFail_WhenSlowerAndEnforcing(self):
        self.set_arm("H", _ALPHA, mean=1200.0)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertIn("::error::", result.stdout)

    def test_Main_ShouldExitZero_WhenSlowerAndObserving(self):
        self.set_arm("H", _ALPHA, mean=1200.0)
        result = self._run()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertNotIn("::error::", result.stdout)
        self.assertEqual("breach", self._outputs()["abab_status"])

    def test_Main_ShouldIgnoreTheAbsoluteGateSwitch_WhenItIsTheOnlyOneSet(self):
        self.set_arm("H", _ALPHA, mean=1200.0)
        result = self._run(env={"PERF_GATE_ENFORCE": "true"})
        self.assertEqual(0, result.returncode, "PERF_GATE_ENFORCE must not arm the ABAB comparison")
        self.assertEqual("false", self._outputs()["abab_enforcing"])

    def test_Main_ShouldExitTwo_WhenTheSwitchIsSpelledOtherThanTrueOrFalse(self):
        # The workflow's continue-on-error compares with the exact string 'true': '1' or 'True'
        # would read ENFORCING here while the job stayed green.
        self.set_arm("H", _ALPHA, mean=2000.0)
        for value in ("1", "True", "yes", "on", " true", "TRUE", "0"):
            with self.subTest(value=value):
                result = self._run(env={"PERF_ABAB_ENFORCE": value})
                self.assertEqual(2, result.returncode, result.stdout)
                self.assertIn("exactly 'true' or 'false'", result.stdout)

    def test_Main_ShouldObserve_WhenTheSwitchIsFalseOrEmpty(self):
        self.set_arm("H", _ALPHA, mean=2000.0)
        for value in ("false", ""):
            with self.subTest(value=value):
                result = self._run(env={"PERF_ABAB_ENFORCE": value})
                self.assertEqual(0, result.returncode, result.stdout)
                self.assertEqual("false", self._outputs()["abab_enforcing"])

    def test_Main_ShouldEnforce_WhenTheFlagIsPassed(self):
        self.set_arm("H", _ALPHA, mean=1200.0)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode, result.stdout)

    def test_Main_ShouldReportUnconfirmed_WhenOnlyOneRoundExceeds(self):
        self.set(2, _ALPHA, mean=1200.0)   # round 1 = (pos 1 R, pos 2 H): 1.2
        result = self._run(enforce=True)    # round 2 = (pos 4 R, pos 3 H): 1.0
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("UNCONFIRMED", self._row(_ALPHA)["verdict"])
        self.assertEqual("1", self._outputs()["abab_unconfirmed"])

    def test_Main_ShouldNotBreach_WhenTheRatioSitsExactlyOnTheThreshold(self):
        self.set_arm("H", _ALPHA, mean=1100.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("ok", self._row(_ALPHA)["verdict"])

    def test_Main_ShouldPairRoundsByPosition_WhenOrderIsRHHR(self):
        # Round k pairs the k-th R with the k-th H: round 1 = (pos 1, pos 2), round 2 = (pos 4,
        # pos 3). The self-ratios are each arm's last round over its first.
        self.set(1, _ALPHA, mean=1000.0)
        self.set(2, _ALPHA, mean=1050.0)
        self.set(3, _ALPHA, mean=1150.0)
        self.set(4, _ALPHA, mean=1040.0)
        self._run()
        row = self._row(_ALPHA)
        self.assertAlmostEqual(1050.0 / 1000.0, row["ratios"][0], places=9)
        self.assertAlmostEqual(1150.0 / 1040.0, row["ratios"][1], places=9)
        self.assertAlmostEqual(1040.0 / 1000.0, row["self"]["R"], places=9)
        self.assertAlmostEqual(1150.0 / 1050.0, row["self"]["H"], places=9)
        self.assertAlmostEqual(math.sqrt(1.05 * 1150.0 / 1040.0), row["geomean"], places=9)

    def test_Main_ShouldNotCrossPair_WhenOnlyTheCrossedRatiosExceed(self):
        self.set(1, _ALPHA, mean=1000.0)
        self.set(2, _ALPHA, mean=1110.0)
        self.set(3, _ALPHA, mean=1110.0)
        self.set(4, _ALPHA, mean=1000.0)
        self.set(1, _BETA, mean=50.0)
        self.set(2, _BETA, mean=52.0)
        self.set(3, _BETA, mean=58.0)
        self.set(4, _BETA, mean=56.0)
        # Beta by position: 52/50 = 1.04, 58/56 = 1.036 -> ok; crossed: 58/50 = 1.16, 52/56 -> no.
        self._run()
        self.assertEqual("ok", self._row(_BETA)["verdict"])
        self.assertEqual("SLOWER", self._row(_ALPHA)["verdict"])

    def test_Main_ShouldReportFasterWithoutFailing_WhenEveryRoundIsBelowTheFasterRatio(self):
        self.set_arm("H", _ALPHA, mean=800.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("FASTER", self._row(_ALPHA)["verdict"])
        self.assertEqual("1", self._outputs()["abab_faster"])
        self.assertEqual("0", self._outputs()["abab_failing"])

    # ---- 8-9. accepted costs ---------------------------------------------------------------
    def test_Main_ShouldScaleTheThreshold_WhenTheRowHasAnAcceptedCost(self):
        self.baseline["abab"]["accepted"] = {_ALPHA: {"ratio": 1.23, "relative_to": "v2.10.1",
                                                      "pr": "#533", "reason": "call index"}}
        self.set_arm("H", _ALPHA, mean=1230.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("ok", self._row(_ALPHA)["verdict"])
        self.assertAlmostEqual(1.23 * 1.10, self._row(_ALPHA)["thresholds"]["slower"], places=9)

    def test_Main_ShouldBreachPastTheAcceptedCost_WhenTheRowGrowsFurther(self):
        self.baseline["abab"]["accepted"] = {_ALPHA: {"ratio": 1.23, "relative_to": "v2.10.1",
                                                      "pr": "#533", "reason": "call index"}}
        self.set_arm("H", _ALPHA, mean=1400.0)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertEqual("SLOWER", self._row(_ALPHA)["verdict"])

    def test_Main_ShouldCentreTheFasterBarOnTheAcceptedCost_WhenTheCostWentAway(self):
        # Accepted 1.23 and measured 1.0: the accepted cost is gone, which is FASTER (stale entry).
        self.baseline["abab"]["accepted"] = {_ALPHA: {"ratio": 1.23, "relative_to": "v2.10.1",
                                                      "reason": "call index"}}
        self._run()
        self.assertEqual("FASTER", self._row(_ALPHA)["verdict"])

    def test_Main_ShouldExitTwo_WhenAnAcceptedCostNamesAnotherReference(self):
        self.baseline["abab"]["accepted"] = {_ALPHA: {"ratio": 1.23, "relative_to": "v2.9.0",
                                                      "reason": "measured against the old tag"}}
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)
        self.assertIn("relative_to", result.stdout)

    # ---- 10. the floor -------------------------------------------------------------------
    def test_Main_ShouldNotJudge_WhenAHeadMeanIsBelowTheFloor(self):
        self.set_arm("H", _TINY, mean=1.9)   # 1.9x at about 1 ns, HEAD still under 2 ns
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        row = self._row(_TINY)
        self.assertFalse(row["judged"])
        self.assertIn("floor", row["not_judged_reason"])
        self.assertEqual("not judged", row["verdict"])
        self.assertIn("Tiny", self._md())

    def test_Main_ShouldNotCallSlower_WhenOnlyTheReferenceIsUnderTheFloorAndHeadIsNear(self):
        # 2.5x by H/R, but only 1.25x the floor: a ratio of a few cycles is not the code.
        self.set_arm("H", _TINY, mean=2.5)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        row = self._row(_TINY)
        self.assertTrue(row["judged"])
        self.assertEqual("ok", row["verdict"])
        self.assertEqual(2.0, row["thresholds"]["slower"])

    def test_Main_ShouldFail_WhenASubFloorRowLeavesTheFloorByTheCoarseBar(self):
        # The review's scenario: 0.72 ns on the reference, 30 ns on HEAD.
        self.set_arm("R", _TINY, mean=0.72)
        self.set_arm("H", _TINY, mean=30.0)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertEqual("SLOWER", self._row(_TINY)["verdict"])
        self.assertIn("ratios to the floor 15.0000, 15.0000", self._md())

    def test_Main_ShouldReportUnconfirmed_WhenASubFloorRowLeavesTheFloorInOneRoundOnly(self):
        self.set(2, _TINY, mean=30.0)
        self.set(3, _TINY, mean=3.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("UNCONFIRMED", self._row(_TINY)["verdict"])

    def test_Main_ShouldNeverCallASubFloorRowFaster_WhenHeadIsFarBelowTheReferenceShare(self):
        self.set_arm("R", _TINY, mean=1.99)
        self.set_arm("H", _TINY, mean=2.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("ok", self._row(_TINY)["verdict"])

    def test_Main_ShouldLeaveSubFloorRowsOutOfTheNoiseStatistics_WhenJudged(self):
        self.set_arm("H", _TINY, mean=2.5)
        self._run()
        summary = self._json()["summary"]
        self.assertEqual(4, summary["judged_rows"])
        self.assertEqual(1, summary["sub_floor_judged"])
        self.assertEqual(6, summary["ratios_count"])   # 3 rows x 2 rounds, Tiny excluded

    def test_Main_ShouldNotJudgeTime_WhenARowOverrideSaysSo(self):
        self.baseline["abab"]["rows"] = {_BETA: {"judge_time": False, "reason": "bimodal"}}
        self.set_arm("H", _BETA, mean=80.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("bimodal", self._row(_BETA)["not_judged_reason"])

    def test_Main_ShouldUseTheRowRatio_WhenARowOverridesIt(self):
        self.baseline["abab"]["rows"] = {_BETA: {"slower_ratio": 1.30, "reason": "noisy row"}}
        self.set_arm("H", _BETA, mean=62.5)    # 1.25: over 1.10, under 1.30
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("ok", self._row(_BETA)["verdict"])

    # ---- 11-13. one side only, reported, never dropped ------------------------------------
    def test_Main_ShouldListHeadOnlyCases_WhenTheReferenceListLacksThem(self):
        new = f"{_TYPE}.Delta"
        self.lists["H"].append(new)
        self.set_arm("H", new, mean=300.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        row = self._row(new)
        self.assertEqual("head-only", row["class"])
        self.assertFalse(row["judged"])
        self.assertIn("Delta", self._md())
        self.assertIn("HEAD only", self._md())
        self.assertEqual("1", self._outputs()["abab_head_only"])

    def test_Main_ShouldListReferenceOnlyCases_WhenHeadListLacksThem(self):
        gone = f"{_TYPE}.Epsilon"
        self.lists["R"].append(gone)
        self.set_arm("R", gone, mean=300.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("reference-only", self._row(gone)["class"])
        self.assertIn("Reference only", self._md())

    def test_Main_ShouldListACaseSetDifference_WhenParametersDiffer(self):
        method = f"{_TYPE}.Param"
        both, extra = f"{method}(N: 1)", f"{method}(N: 2)"
        for arm in ("R", "H"):
            self.lists[arm].append(method)
            self.set_arm(arm, both, mean=400.0)
        self.set_arm("H", extra, mean=800.0)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("both", self._row(both)["class"])
        row = self._row(extra)
        self.assertEqual("case-set-differs", row["class"])
        self.assertEqual("H", row["arm"])
        self.assertIn("Case set differs", self._md())

    # ---- 14-19. structural ---------------------------------------------------------------
    def test_Main_ShouldBeStructural_WhenARoundLacksACaseItsArmDeclares(self):
        del self.values[3][_BETA]   # H round 2 only
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("Beta" in s and "3-H" in s for s in self._structural()),
                        self._structural())
        self.assertEqual("incomplete", self._row(_BETA)["class"])

    def test_Main_ShouldBeStructural_WhenAListedMethodIsNeverMeasured(self):
        self.lists["R"].append(f"{_TYPE}.Zeta")
        self.lists["H"].append(f"{_TYPE}.Zeta")
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("Zeta" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenHeadsListLacksADeclaredSourceType(self):
        other = "Verbara.Sdk.Benchmarks.OtherBenchmark"
        self.baseline["sources"]["other"] = other
        self.baseline["abab"]["rows"] = {}
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any(other in s and "list-H" in s for s in self._structural()),
                        self._structural())

    def test_Main_ShouldNotBeStructural_WhenOnlyTheReferenceLacksASourceType(self):
        other = "Verbara.Sdk.Benchmarks.OtherBenchmark"
        self.baseline["sources"]["other"] = other
        self.lists["H"].append(f"{other}.Run")
        self.set_arm("H", f"{other}.Run", mean=500.0)
        result = self._run(enforce=True)
        # The class is new since the tag: every case is HEAD only, nothing is structural. The
        # report of that class is absent from the R invocations, which is expected.
        self.assertEqual([], self._structural())
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("head-only", self._row(f"{other}.Run")["class"])

    def test_Main_ShouldBeStructural_WhenAReportCaseIsNotInItsArmList(self):
        self.lists["H"].remove(_BETA)
        self.lists["R"].remove(_BETA)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("Beta" in s and "list" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenAnInvocationHasNoReport(self):
        self.no_report.add(4)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("4-R" in s for s in self._structural()), self._structural())

    def test_Main_ShouldSayTheInvocationNeverRan_WhenItLeftNoDirectory(self):
        # Mutation: dropping the "no directory" check still fails closed through the missing report,
        # but the reason a reader needs (the invocation never ran) is lost.
        self.no_report.add(4)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any(s.startswith("[4-R]") and "never ran" in s for s in self._structural()),
                        self._structural())

    def test_Main_ShouldBeStructural_WhenTheRootHoldsOnlyTheManifest(self):
        self.no_report.update({1, 2, 3, 4})
        self.lists = {"R": None, "H": None}
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertGreaterEqual(len(self._structural()), 2)

    def test_Main_ShouldBeStructural_WhenAListIsEmpty(self):
        self.lists["R"] = []
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("list-R" in s for s in self._structural()), self._structural())

    def test_Main_ShouldBeStructural_WhenAMeanIsNotFinite(self):
        for stats in (None, {"Mean": float("nan")}, {"Mean": "1000 ns"}, {"N": 3}):
            with self.subTest(stats=stats):
                self.raw_stats = {(2, _ALPHA): stats}
                result = self._run(enforce=True)
                self.assertEqual(1, result.returncode, result.stdout)
                self.assertTrue(any("Alpha" in s for s in self._structural()),
                                self._structural())

    def test_Main_ShouldBeStructural_WhenAnArmRanOnAnotherRuntime(self):
        self.host[1]["RuntimeVersion"] = ".NET 10.0.16 (10.0.16, 10.0.1626.1)"
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("runtime" in s and "1-R" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenEveryInvocationRanOnTheSameUndeclaredRuntime(self):
        # Equal to each other is not enough: both arms on 10.0.16 is still not the experiment
        # baseline.json `environment` declares.
        for pos in self.host:
            self.host[pos]["RuntimeVersion"] = ".NET 10.0.16 (10.0.16, 10.0.1626.1)"
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertEqual(4, sum(1 for s in self._structural() if "not the declared 10.0.15" in s))

    def test_Main_ShouldBeStructural_WhenAnArmRanOnAnotherCoreCount(self):
        self.host[3]["LogicalCoreCount"] = 8
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("core" in s and "3-H" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenAnArmRanOnAnotherSdk(self):
        self.host[4]["DotNetCliVersion"] = "10.0.405"
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("SDK" in s and "4-R" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenAnArmLogNamesAnotherIsa(self):
        self.isa[2] = "x86-64-v4"
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("x86-64-v4" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenNoLogNamesTheIsa(self):
        self.isa[3] = None
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("ISA" in s and "3-H" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenTheArmsUseDifferentBenchmarkDotNetVersions(self):
        self.host[1]["BenchmarkDotNetVersion"] = "0.15.7"
        self.host[4]["BenchmarkDotNetVersion"] = "0.15.7"
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("BenchmarkDotNet" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenTheArmsRanOnDifferentProcessors(self):
        self.host[2]["ProcessorName"] = "AMD EPYC 9V74"
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("processor" in s.lower() for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenAnInvocationTimedOut(self):
        self._materialize()
        self.manifest["invocations"][2]["timed_out"] = True
        self.manifest["invocations"][2]["exit_code"] = 124
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("timed out" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenTheReferenceDirectoryIsAtAnotherCommit(self):
        self.manifest["reference_dir_sha"] = "c" * 40
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("commit" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenNeitherReferenceCommitIsRecorded(self):
        self.manifest["reference_sha"] = None
        self.manifest["reference_dir_sha"] = None
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("not the declared reference" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenOnlyTheTagCommitIsUnknown(self):
        self.manifest["reference_sha"] = None
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("not the declared reference" in s for s in self._structural()))

    def test_Main_ShouldReportStructuralWithoutFailing_WhenObserving(self):
        self.no_report.add(4)
        result = self._run()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("::warning::", result.stdout)
        self.assertEqual("breach", self._outputs()["abab_status"])

    # ---- 20-23. bytes in the job ---------------------------------------------------------
    def test_Main_ShouldReportMoreBytes_WhenEveryRoundAllocatesMore(self):
        self.set_arm("H", _ALPHA, allocated=176)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        row = self._row(_ALPHA)
        self.assertEqual("MORE-BYTES", row["bytes_verdict"])
        self.assertEqual([8, 8], row["bytes_delta"])

    def test_Main_ShouldReportFewerBytesWithoutFailing_WhenEveryRoundAllocatesLess(self):
        self.set_arm("H", _ALPHA, allocated=160)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("FEWER-BYTES", self._row(_ALPHA)["bytes_verdict"])

    def test_Main_ShouldNotFailOnBytes_WhenOnlyOneRoundAllocatesMore(self):
        self.set(2, _ALPHA, allocated=176)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("ok", self._row(_ALPHA)["bytes_verdict"])

    def test_Main_ShouldRespectTheBytesBound_WhenTheRowCarriesOne(self):
        self.set_arm("H", _GAMMA, allocated=500900)
        self._run(enforce=True)
        self.assertEqual("ok", self._row(_GAMMA)["bytes_verdict"])
        self.set_arm("H", _GAMMA, allocated=501100)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertEqual("MORE-BYTES", self._row(_GAMMA)["bytes_verdict"])

    def test_Main_ShouldNotJudgeBytes_WhenTheRowHasNoBytesEntry(self):
        self.set_arm("H", _BETA, allocated=4096)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        row = self._row(_BETA)
        self.assertFalse(row["bytes_judged"])
        self.assertEqual([4096, 4096], row["bytes_delta"])

    def test_Main_ShouldFlagABytesAnomaly_WhenAnArmDisagreesWithItself(self):
        self.set(1, _ALPHA, allocated=168)
        self.set(4, _ALPHA, allocated=176)
        self.set(2, _ALPHA, allocated=176)
        self.set(3, _ALPHA, allocated=176)
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, "one round's delta is zero: not MORE-BYTES")
        self.assertEqual("1", self._outputs()["abab_bytes_anomalies"])
        self.assertIn("Bytes anomalies", self._md())

    def test_Main_ShouldBeStructural_WhenABytesRowCarriesNoMemoryOutput(self):
        self.set(3, _ALPHA)
        self.values[3][_ALPHA][1] = None
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("Memory" in s for s in self._structural()))

    # ---- 24. a benchmark that changed itself ---------------------------------------------
    def test_Main_ShouldBeStructural_WhenTheBenchmarkSourceDiffersAndNoRowOfTheClassIsJudged(self):
        # The review's probe: HEAD 5x slower and 10 MB heavier on every row, one source edit.
        self.manifest["changed_benchmark_paths"] = ["Tests/Verbara.Sdk.Benchmarks/FakeBenchmark.cs"]
        for case, (mean, allocated) in _DEFAULTS.items():
            self.set_arm("H", case, mean=mean * 5, allocated=allocated + 10_000_000)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        row = self._row(_ALPHA)
        self.assertFalse(row["judged"])
        self.assertFalse(row["bytes_judged"])
        self.assertTrue(row["blind"])
        self.assertIn("benchmark's own change", row["not_judged_reason"])
        self.assertAlmostEqual(5.0, row["geomean"], places=6)
        self.assertTrue(any(_TYPE in s and "not compared" in s for s in self._structural()),
                        self._structural())
        self.assertTrue(any("no row was judged" in s for s in self._structural()))
        outputs = self._outputs()
        self.assertEqual("breach", outputs["abab_status"])
        self.assertEqual("4", outputs["abab_blind"])
        self.assertEqual(4, self._json()["summary"]["blind"])

    def test_Main_ShouldReportBreachNotPass_WhenTheBenchmarkSourceDiffersAndObserving(self):
        self.manifest["changed_benchmark_paths"] = ["Tests/Verbara.Sdk.Benchmarks/FakeBenchmark.cs"]
        result = self._run()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("breach", self._outputs()["abab_status"])
        self.assertIn("Not compared: the benchmark itself differs", self._md())

    def test_Main_ShouldNotJudgeTheRow_WhenItsJobDiffersBetweenTheArms(self):
        for pos in (2, 3):
            self.job[(pos, _BETA)] = "Job-ABCDEF(IterationCount=15, LaunchCount=1)"
        self.set_arm("H", _BETA, mean=80.0)
        result = self._run(enforce=True)
        # Alpha and Gamma are still judged, so the class is compared: a finding, not a failure.
        self.assertEqual(0, result.returncode, result.stdout)
        row = self._row(_BETA)
        self.assertFalse(row["judged"])
        self.assertTrue(row["blind"])
        self.assertIn("different BenchmarkDotNet jobs", row["not_judged_reason"])
        self.assertEqual("ok", self._row(_ALPHA)["verdict"])
        self.assertEqual("breach", self._outputs()["abab_status"])
        self.assertEqual("1", self._outputs()["abab_blind"])

    def test_Main_ShouldBeStructural_WhenEveryRowRanUnderAnotherJobInHead(self):
        # A HEAD-side global config (a ManualConfig in Program.cs) changes every case's job.
        for case in _DEFAULTS:
            for pos in (2, 3):
                self.job[(pos, case)] = "Job-ABCDEF(IterationCount=15, LaunchCount=1)"
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("not compared" in s for s in self._structural()))

    def test_Main_ShouldNotJudgeTheRow_WhenAnInvocationDoesNotRecordItsJob(self):
        self.job[(4, _BETA)] = None
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("DisplayInfo", self._row(_BETA)["not_judged_reason"])

    def test_Main_ShouldBeStructural_WhenNoRowIsJudgedOnTime(self):
        self.baseline["abab"]["rows"] = {
            case: {"judge_time": False, "reason": "probe"} for case in (_ALPHA, _BETA, _GAMMA)}
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("no row was judged" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenTheArmsRanOnDifferentHardwareIntrinsics(self):
        for case in _DEFAULTS:
            self.intrinsics[(3, case)] = "AVX512F+AVX512BW,AVX2 VectorSize=512"
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("hardware intrinsics" in s for s in self._structural()))

    def test_Main_ShouldBeStructural_WhenACaseDoesNotRecordItsHardwareIntrinsics(self):
        self.intrinsics[(1, _ALPHA)] = None
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any("does not record its hardware intrinsics" in s
                            for s in self._structural()), self._structural())

    def test_Main_ShouldNoticeAProjectChange_WhenAnotherBenchmarkFileDiffers(self):
        self.manifest["changed_benchmark_paths"] = [
            "Tests/Verbara.Sdk.Benchmarks/Verbara.Sdk.Benchmarks.csproj",
            "Tests/Verbara.Sdk.Benchmarks/baseline.json"]
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("Verbara.Sdk.Benchmarks.csproj", self._md())
        project = [n for n in self._json()["notices"] if "csproj" in n]
        self.assertEqual(1, len(project), self._json()["notices"])
        self.assertNotIn("baseline.json", project[0])
        self.assertEqual("ok", self._row(_ALPHA)["verdict"])

    # ---- 25. the manifest ----------------------------------------------------------------
    def test_Main_ShouldExitTwo_WhenTheManifestOrderDiffersFromTheDeclaration(self):
        self.manifest["order"] = ["R", "H", "R", "H"]
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)

    def test_Main_ShouldExitTwo_WhenTheManifestIsMissing(self):
        self.write_manifest = False
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)

    def test_Main_ShouldExitTwo_WhenTheManifestInvocationsDisagreeWithItsOrder(self):
        self._materialize()
        self.manifest["invocations"][1]["arm"] = "R"
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)

    # ---- 26-27. the abab block -----------------------------------------------------------
    def test_LoadAbab_ShouldExitTwo_WhenTheBlockIsInvalid(self):
        cases = {
            "missing block": lambda b: b.pop("abab"),
            "unknown key": lambda b: b["abab"].update(rounds=2),
            "slower_ratio 1.0": lambda b: b["abab"].update(slower_ratio=1.0),
            "faster_ratio 1.0": lambda b: b["abab"].update(faster_ratio=1.0),
            "faster_ratio 0": lambda b: b["abab"].update(faster_ratio=0),
            "negative floor": lambda b: b["abab"].update(min_mean_ns=-1),
            "sub-floor bar at the slower bar": lambda b: b["abab"].update(
                sub_floor_slower_ratio=1.10),
            "sub-floor bar missing": lambda b: b["abab"].pop("sub_floor_slower_ratio"),
            "timeout zero": lambda b: b["abab"].update(invocation_timeout_s=0),
            "launch count missing": lambda b: b["abab"].pop("launch_count"),
            "launch count zero": lambda b: b["abab"].update(launch_count=0),
            "launch count string": lambda b: b["abab"].update(launch_count="2"),
            "launch count float": lambda b: b["abab"].update(launch_count=2.0),
            "launch count bool": lambda b: b["abab"].update(launch_count=True),
            "one round only": lambda b: b["abab"].update(order=["R", "H"]),
            "unequal R and H": lambda b: b["abab"].update(order=["R", "H", "H", "R", "R"]),
            "unknown arm": lambda b: b["abab"].update(order=["R", "H", "X", "R"]),
            "override without reason": lambda b: b["abab"].update(
                rows={_ALPHA: {"slower_ratio": 1.2}}),
            "override unknown key": lambda b: b["abab"].update(
                rows={_ALPHA: {"band": 1.2, "reason": "x"}}),
            "override ratio at 1": lambda b: b["abab"].update(
                rows={_ALPHA: {"slower_ratio": 1.0, "reason": "x"}}),
            "row of an undeclared type": lambda b: b["abab"].update(
                rows={"Verbara.Sdk.Benchmarks.Other.Alpha": {"judge_time": False,
                                                              "reason": "x"}}),
            "accepted without reason": lambda b: b["abab"].update(
                accepted={_ALPHA: {"ratio": 1.2, "relative_to": "v2.10.1"}}),
            "accepted ratio zero": lambda b: b["abab"].update(
                accepted={_ALPHA: {"ratio": 0, "relative_to": "v2.10.1", "reason": "x"}}),
            "bad tag spelling": lambda b: b["abab"].update(reference_tag="2.10.1"),
            "prerelease tag": lambda b: b["abab"].update(reference_tag="v2.10.1-rc.1"),
        }
        for label, mutate in cases.items():
            with self.subTest(case=label):
                self.baseline = _deep(_BASELINE)
                mutate(self.baseline)
                if label in ("bad tag spelling", "prerelease tag"):
                    self.manifest["reference_tag"] = self.baseline["abab"]["reference_tag"]
                result = self._run()
                self.assertEqual(2, result.returncode, f"{label}: {result.stdout}")
                if label.startswith("launch count") and label != "launch count missing":
                    # Refused by the block's own check, not by the manifest disagreeing with it.
                    self.assertIn("abab.launch_count\" must be", result.stdout, label)

    def test_Main_ShouldExitTwo_InObservingModeToo_WhenMisconfigured(self):
        self.baseline["abab"]["slower_ratio"] = 0.9
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)
        self.assertIn("::error::", result.stdout)

    def test_Main_ShouldExitTwo_WhenTheBaselineFailsTheAbsoluteComparatorsValidation(self):
        self.baseline["benchmarks"][_ALPHA]["tolerance_pct"] = "wide"
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)

    def test_Main_ShouldExitTwo_WhenTheManifestNamesAnotherReferenceTag(self):
        self.manifest["reference_tag"] = "v2.10.0"
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)

    def test_Main_ShouldExitTwo_WhenArgumentsAreMissingOrUnknown(self):
        self._materialize()
        for argv in ([], ["--baseline", self.baseline_path], ["--bogus"]):
            with self.subTest(argv=argv):
                result = subprocess.run([sys.executable, _SCRIPT] + argv, capture_output=True,
                                        text=True, check=False)
                self.assertEqual(2, result.returncode, result.stdout)

    # ---- 28-30. what the run leaves for the 5-run evaluation -----------------------------
    def test_Main_ShouldWriteTheJsonSchema_WhenAsked(self):
        self.set(2, _ALPHA, mean=1020.0)   # r1 = 1.02
        self.set(3, _ALPHA, mean=990.0)    # r2 = 0.99 (R2 = 1000)
        self.set(4, _BETA, mean=56.0)      # R self = 1.12, r2 = 50/56, outside the band
        self._run()
        doc = self._json()
        self.assertEqual({"meta", "summary", "rows", "structural", "notices"}, set(doc))
        self.assertEqual(1, doc["meta"]["schema"])
        self.assertEqual(_META_KEYS, set(doc["meta"]))
        self.assertEqual(_SUMMARY_KEYS, set(doc["summary"]))
        for row in doc["rows"]:
            self.assertEqual(_ROW_KEYS, set(row), row["name"])
        summary = doc["summary"]
        # Judged: Alpha, Beta, Gamma (Tiny is under the floor).
        self.assertEqual(3, summary["judged_rows"])
        self.assertEqual(6, summary["ratios_count"])
        logs = sorted(abs(math.log(r)) for r in (1.02, 0.99, 1.0, 50.0 / 56.0, 1.0, 1.0))
        self.assertAlmostEqual((logs[2] + logs[3]) / 2, summary["median_abs_log_ratio"], places=9)
        self.assertAlmostEqual(1.12 ** (1 / 3), summary["order_effect_geomean"], places=9)
        # The noise clause reads g, the statistic the verdict uses: one g per judged row.
        self.assertEqual(3, summary["geomeans_count"])
        g_logs = sorted(abs(math.log(g)) for g in (math.sqrt(1.02 * 0.99),
                                                   math.sqrt(50.0 / 56.0), 1.0))
        self.assertAlmostEqual(g_logs[1], summary["median_abs_log_geomean"], places=9)
        self.assertAlmostEqual(g_logs[-1], summary["p95_abs_log_geomean"], places=9)
        self.assertAlmostEqual(0.0, summary["share_geomean_outside_band"], places=9)
        self.assertEqual(1, summary["unconfirmed"])
        self.assertEqual(0, summary["lone_rounds"])
        alpha = self._row(_ALPHA)
        self.assertEqual([1000.0, 1000.0], alpha["means"]["R"])
        self.assertEqual([1020.0, 990.0], alpha["means"]["H"])
        self.assertEqual([168, 168], alpha["bytes"]["H"])

    def test_Main_ShouldSayTheRunIsAnAATest_WhenNoCompiledPathDiffers(self):
        self._run()
        self.assertIn("No compiled file differs from the reference", self._md())
        self.assertTrue(self._json()["meta"]["aa_run"])

    def test_Main_ShouldListTheCompiledChanges_WhenSomeDiffer(self):
        self.manifest["changed_compiled_paths"] = ["src/Verbara.Sdk/X.cs"]
        self._run()
        self.assertNotIn("No compiled file differs from the reference", self._md())
        self.assertIn("src/Verbara.Sdk/X.cs", self._md())
        self.assertFalse(self._json()["meta"]["aa_run"])

    def test_Main_ShouldNoticeAStaleReferenceTag_WhenANewerTagExists(self):
        self.manifest["newest_tag"] = "v2.11.0"
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, "a stale tag is a notice, never a failure")
        self.assertIn("v2.11.0", self._md())
        self.assertTrue(self._json()["meta"]["stale_reference"])
        self.assertIn("::notice::", result.stdout)

    def test_Main_ShouldWriteGithubOutput_WhenRun(self):
        self.set_arm("H", _ALPHA, mean=1200.0)
        self._run()
        outputs = self._outputs()
        for key in ("abab_status", "abab_slower", "abab_more_bytes", "abab_faster",
                    "abab_fewer_bytes", "abab_unconfirmed", "abab_structural", "abab_failing",
                    "abab_enforcing", "abab_head_only", "abab_reference_only",
                    "abab_bytes_anomalies", "abab_blind"):
            self.assertIn(key, outputs)
        self.assertEqual("breach", outputs["abab_status"])
        self.assertEqual("1", outputs["abab_slower"])
        self.assertEqual("1", outputs["abab_failing"])
        self.assertEqual("false", outputs["abab_enforcing"])

    def test_Main_ShouldNameTheModeAndTheSwitch_WhenRenderingMarkdown(self):
        self._run()
        body = self._md()
        self.assertIn("In-job comparison: HEAD against v2.10.1 (R H H R)", body)
        self.assertNotIn("(R H H R R H)", body)
        self.assertIn("OBSERVING", body)
        self.assertIn("PERF_ABAB_ENFORCE", body)
        self.assertIn("AMD EPYC 7763", body)
        self.assertIn("x86-64-v3", body)

    # ---- 31. three rounds: the geometric mean, confirmed by a majority -------------------
    _THREE = ["R", "H", "H", "R", "R", "H"]
    _H_OF_ROUND = {1: 2, 2: 3, 3: 6}   # round k pairs the k-th R (1, 4, 5) with the k-th H

    def use_three_rounds(self):
        self.order = list(self._THREE)
        self.baseline["abab"]["order"] = list(self._THREE)
        self.manifest["order"] = list(self._THREE)
        self.values = {pos: {name: list(value) for name, value in _DEFAULTS.items()}
                       for pos in range(1, len(self.order) + 1)}
        self.host = {pos: {} for pos in self.values}
        self.isa = {pos: "x86-64-v3" for pos in self.values}

    def set_rounds(self, case, *ratios):
        """HEAD's mean in round k is the reference's (the default) times ratios[k-1]."""
        reference = _DEFAULTS[case][0]
        for k, ratio in enumerate(ratios, start=1):
            self.set(self._H_OF_ROUND[k], case, mean=round(reference * ratio, 6))

    def test_Main_ShouldReportSlower_WhenTheGeomeanAndTwoOfThreeRoundsExceed(self):
        self.use_three_rounds()
        self.set_rounds(_ALPHA, 1.2, 1.2, 1.0)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        row = self._row(_ALPHA)
        self.assertEqual("SLOWER", row["verdict"])
        self.assertEqual(3, len(row["ratios"]))
        self.assertAlmostEqual((1.2 * 1.2) ** (1 / 3), row["geomean"], places=9)
        self.assertIn("In-job comparison: HEAD against v2.10.1 (R H H R R H)", self._md())

    def test_Main_ShouldReportUnconfirmed_WhenOneSpikeAloneLiftsTheGeomean(self):
        self.use_three_rounds()
        self.set_rounds(_ALPHA, 1.5, 1.0, 1.0)   # g = 1.1447 over 1.10, one round of three
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("UNCONFIRMED", self._row(_ALPHA)["verdict"])
        self.assertEqual("1", self._outputs()["abab_unconfirmed"])
        self.assertEqual("0", self._outputs()["abab_slower"])

    def test_Main_ShouldReportUnconfirmed_WhenAMajorityExceedsButTheGeomeanDoesNot(self):
        self.use_three_rounds()
        self.set_rounds(_ALPHA, 1.11, 1.11, 0.95)   # g = 1.0540: two rounds over, g inside
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("UNCONFIRMED", self._row(_ALPHA)["verdict"])

    def test_Main_ShouldCountALoneRound_WhenOneRoundExceedsAndTheGeomeanIsInside(self):
        self.use_three_rounds()
        self.set_rounds(_ALPHA, 1.15, 1.0, 1.0)   # g = 1.0477
        self.set_rounds(_BETA, 1.0, 0.85, 1.0)    # g = 0.9473, one round under 0.909
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        for case in (_ALPHA, _BETA):
            row = self._row(case)
            self.assertEqual("ok", row["verdict"], case)
            self.assertEqual(1, row["lone_rounds"], case)
        self.assertEqual(0, self._row(_GAMMA)["lone_rounds"])
        summary = self._json()["summary"]
        self.assertEqual(2, summary["lone_rounds"])
        self.assertEqual(0, summary["unconfirmed"])
        self.assertEqual("pass", self._outputs()["abab_status"])

    def test_Main_ShouldReportFasterSymmetrically_WhenThreeRoundsRun(self):
        self.use_three_rounds()
        self.set_rounds(_ALPHA, 0.8, 0.8, 1.0)    # g = 0.8618, two of three under
        self.set_rounds(_BETA, 0.7, 1.0, 1.0)     # g = 0.8879 by one spike
        self.set_rounds(_GAMMA, 0.9, 0.9, 1.0)    # two of three under, g = 0.9322 inside
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("FASTER", self._row(_ALPHA)["verdict"])
        self.assertEqual("UNCONFIRMED", self._row(_BETA)["verdict"])
        self.assertEqual("UNCONFIRMED", self._row(_GAMMA)["verdict"])
        self.assertEqual("0", self._outputs()["abab_failing"])

    def test_Main_ShouldJudgeASubFloorRowByTheGeomeanAndTheMajority_WhenThreeRoundsRun(self):
        self.use_three_rounds()
        self.set_arm("R", _TINY, mean=0.72)
        for pos, mean in ((2, 30.0), (3, 30.0), (6, 2.0)):   # to the floor: 15, 15, 1
            self.set(pos, _TINY, mean=mean)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertEqual("SLOWER", self._row(_TINY)["verdict"])
        self.set(3, _TINY, mean=2.0)                          # 15, 1, 1: g = 2.466, one round
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("UNCONFIRMED", self._row(_TINY)["verdict"])
        self.set(2, _TINY, mean=3.0)                          # 1.5, 1, 1: inside
        self._run(enforce=True)
        self.assertEqual("ok", self._row(_TINY)["verdict"])

    def test_Main_ShouldKeepTodaysVerdicts_WhenTheOrderHasTwoRounds(self):
        # The regression control: with two rounds, "g beyond and a majority beyond" is "every
        # round beyond", and one round beyond is UNCONFIRMED whatever g says.
        cases = {(1.2, 1.2): "SLOWER", (1.2, 1.0): "UNCONFIRMED", (1.25, 1.0): "UNCONFIRMED",
                 (1.05, 1.15): "UNCONFIRMED", (0.8, 0.8): "FASTER", (0.8, 1.0): "UNCONFIRMED",
                 (0.7, 1.0): "UNCONFIRMED", (1.2, 0.8): "UNCONFIRMED", (1.1, 1.1): "ok",
                 (1.0, 1.0): "ok", (1.09, 0.92): "ok"}
        for (r1, r2), verdict in cases.items():
            with self.subTest(ratios=(r1, r2)):
                self.set(2, _ALPHA, mean=round(1000.0 * r1, 6))   # round 1 = (pos 1, pos 2)
                self.set(3, _ALPHA, mean=round(1000.0 * r2, 6))   # round 2 = (pos 4, pos 3)
                result = self._run(enforce=True)
                self.assertEqual(1 if verdict == "SLOWER" else 0, result.returncode,
                                 result.stdout)
                self.assertEqual(verdict, self._row(_ALPHA)["verdict"])

    def test_Main_ShouldComputeTheNoiseStatisticsOnTheGeomean_WhenThreeRoundsRun(self):
        self.use_three_rounds()
        self.set_rounds(_ALPHA, 1.02, 0.99, 1.0)
        self.set_rounds(_BETA, 1.3, 1.3, 1.3)
        self.set_rounds(_GAMMA, 1.12, 1.0, 1.0)   # a lone round, g = 1.0385
        self._run()
        summary = self._json()["summary"]
        self.assertEqual(_SUMMARY_KEYS, set(summary))
        self.assertEqual(9, summary["ratios_count"])
        self.assertEqual(3, summary["geomeans_count"])
        g_alpha = (1.02 * 0.99) ** (1 / 3)
        g_gamma = 1.12 ** (1 / 3)
        logs = sorted(abs(math.log(g)) for g in (g_alpha, 1.3, g_gamma))
        self.assertAlmostEqual(logs[1], summary["median_abs_log_geomean"], places=9)
        self.assertAlmostEqual(math.log(1.3), summary["p95_abs_log_geomean"], places=9)
        self.assertAlmostEqual(1 / 3, summary["share_geomean_outside_band"], places=9)
        ratio_logs = sorted(abs(math.log(r)) for r in (1.02, 0.99, 1.0, 1.3, 1.3, 1.3,
                                                       1.12, 1.0, 1.0))
        self.assertAlmostEqual(ratio_logs[4], summary["median_abs_log_ratio"], places=9)
        self.assertEqual(1, summary["lone_rounds"])
        self.assertEqual(1, summary["slower"])
        body = self._md()
        self.assertIn("median |ln g|", body)
        self.assertIn("1 lone round", body)

    def test_Main_ShouldStillJudgeBytes_WhenARowIsNotJudgedOnTime(self):
        self.use_three_rounds()
        self.baseline["abab"]["rows"] = {_ALPHA: {"judge_time": False, "reason": "bimodal"}}
        self.set_arm("H", _ALPHA, mean=2000.0, allocated=176)
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        row = self._row(_ALPHA)
        self.assertFalse(row["judged"])
        self.assertEqual("not judged", row["verdict"])
        self.assertIn("bimodal", row["not_judged_reason"])
        self.assertEqual("MORE-BYTES", row["bytes_verdict"])
        self.assertEqual([8, 8, 8], row["bytes_delta"])
        self.assertEqual(0, self._json()["summary"]["slower"])

    def test_Main_ShouldRequireEveryRoundForBytes_WhenThreeRoundsRun(self):
        self.use_three_rounds()
        self.set(2, _ALPHA, allocated=176)
        self.set(3, _ALPHA, allocated=176)    # rounds 1 and 2 over, round 3 exact
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        row = self._row(_ALPHA)
        self.assertEqual("ok", row["bytes_verdict"])
        self.assertEqual([8, 8, 0], row["bytes_delta"])
        self.set(3, _ALPHA, allocated=160)    # FEWER needs every round too
        self.set(2, _ALPHA, allocated=160)
        self._run(enforce=True)
        self.assertEqual("ok", self._row(_ALPHA)["bytes_verdict"])

    # ---- 32. the declared launch count (two launches per invocation) ----------------------
    def use_two_launches(self):
        self.baseline["abab"]["launch_count"] = 2

    def test_Main_ShouldPass_WhenEveryInvocationShowsTheDeclaredTwoLaunches(self):
        self.use_two_launches()
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self._structural())
        self.assertEqual(2, self._json()["meta"]["launch_count"])
        self.assertIn("2 launch(es) per invocation", self._md())

    def test_Main_ShouldBeStructural_WhenTheJobShowsOneLaunchAndTwoAreDeclared(self):
        # The override did not apply: BenchmarkDotNet ran the source's ShortRun as written.
        self.use_two_launches()
        for case in _DEFAULTS:
            self.job[(3, case)] = _JOB
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        problems = [s for s in self._structural() if s.startswith("[3-H]") and "launch" in s]
        self.assertEqual(1, len(problems), self._structural())
        self.assertIn("LaunchCount=1", problems[0])
        self.assertIn("4 case(s)", problems[0])

    def test_Main_ShouldBeStructural_WhenTheJobSaysTwoLaunchesButOneRan(self):
        self.use_two_launches()
        self.results[(4, _ALPHA)] = {1: 3}
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any(s.startswith("[4-R]") and "launch" in s and _ALPHA in s
                            for s in self._structural()), self._structural())

    def test_Main_ShouldBeStructural_WhenALaunchHasTooFewResultIterations(self):
        self.use_two_launches()
        self.results[(2, _BETA)] = {1: 3, 2: 2}
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any(s.startswith("[2-H]") and "Result" in s and _BETA in s
                            for s in self._structural()), self._structural())

    def test_Main_ShouldBeStructural_WhenThreeLaunchesRanAndTwoAreDeclared(self):
        self.use_two_launches()
        self.results[(1, _GAMMA)] = {1: 3, 2: 3, 3: 3}
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any(s.startswith("[1-R]") and _GAMMA in s for s in self._structural()),
                        self._structural())

    def test_Main_ShouldBeStructural_WhenACaseRecordsNoMeasurements(self):
        self.use_two_launches()
        self.results[(2, _ALPHA)] = None
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any(s.startswith("[2-H]") and "Measurements" in s
                            for s in self._structural()), self._structural())

    def test_Main_ShouldBeStructural_WhenTheJobTextNamesNoLaunchCount(self):
        self.use_two_launches()
        self.job[(4, _BETA)] = "Job-ABCDEF(IterationCount=3)"
        self.results[(4, _BETA)] = {1: 3, 2: 3}
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertTrue(any(s.startswith("[4-R]") and "LaunchCount" in s
                            for s in self._structural()), self._structural())

    def test_Main_ShouldStillPass_WhenOneLaunchIsDeclaredAndShown(self):
        # The fixture default: launch_count 1 with ShortRun(..., LaunchCount=1, ...).
        result = self._run(enforce=True)
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual(1, self._json()["meta"]["launch_count"])

    def test_Main_ShouldBeStructural_WhenTwoLaunchesRanAndOneIsDeclared(self):
        for case in _DEFAULTS:
            for pos in range(1, len(self.order) + 1):
                self.job[(pos, case)] = _JOB.replace("LaunchCount=1", "LaunchCount=2")
        result = self._run(enforce=True)
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertEqual(len(self.order),
                         sum(1 for s in self._structural() if "launch" in s), self._structural())

    def test_Main_ShouldExitTwo_WhenTheManifestRecordsAnotherLaunchCount(self):
        self.use_two_launches()
        self.manifest_launch_count = 1
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)
        self.assertIn("launch_count", result.stdout)

    def test_Main_ShouldExitTwo_WhenTheManifestRecordsNoLaunchCount(self):
        self.manifest_launch_count = None
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)


def _code_lines(text):
    return "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("#"))


def _step(code, name):
    """The body of the workflow step called `name`, up to the next step."""
    match = re.search(rf"(?m)^[ ]+- name: {re.escape(name)}\n((?:(?![ ]+- (?:name|uses):).*\n)*)",
                      code)
    if match is None:
        raise AssertionError(f"workflow has no step named {name!r}")
    return match.group(1)


_ABAB_STEPS = ("Read the declared in-job reference",
               "Check out the reference tag beside HEAD",
               "Build the reference benchmarks (Release)",
               "Run the reference and HEAD alternated (R H H R R H)",
               "Compare HEAD against the reference in this job",
               "Publish the in-job comparison to the run summary")


class CommittedAbabContractTests(unittest.TestCase):
    """Pins the shipped baseline.json `abab` block and the workflow steps that read it."""

    @classmethod
    def setUpClass(cls):
        with open(_REAL_BASELINE, encoding="utf-8") as handle:
            cls.doc = json.load(handle)
        with open(_WORKFLOW, encoding="utf-8") as handle:
            cls.workflow = handle.read()
        cls.code = _code_lines(cls.workflow)

    def test_Baseline_ShouldDeclareAValidAbabBlock(self):
        abab = self.doc["abab"]
        self.assertRegex(abab["reference_tag"], r"^v\d+\.\d+\.\d+$")
        # An empty root with a valid manifest: every invocation is missing, so the comparator
        # reports structural failures (exit 1 enforcing) — exit 2 would mean the block is invalid.
        tmp = tempfile.mkdtemp()
        try:
            order = abab["order"]
            manifest = {"schema": 1, "reference_tag": abab["reference_tag"],
                        "reference_sha": None, "reference_dir_sha": None, "head_sha": None,
                        "newest_tag": None, "order": order,
                        "invocations": [{"position": i, "arm": arm, "dir": f"{i}-{arm}",
                                         "started": None, "ended": None, "exit_code": 1,
                                         "timed_out": False}
                                        for i, arm in enumerate(order, start=1)],
                        "lists": {"R": {"exit_code": 1}, "H": {"exit_code": 1}},
                        "launch_count": abab.get("launch_count"),
                        "changed_benchmark_paths": None, "changed_compiled_paths": None}
            with open(os.path.join(tmp, "manifest.json"), "w", encoding="utf-8") as handle:
                json.dump(manifest, handle)
            environ = dict(os.environ)
            environ.pop("GITHUB_OUTPUT", None)
            result = subprocess.run([sys.executable, _SCRIPT, "--baseline", _REAL_BASELINE,
                                     "--abab-root", tmp, "--enforce"],
                                    capture_output=True, text=True, check=False, env=environ)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)

    def test_Baseline_ShouldDeclareThreeRounds(self):
        self.assertEqual(["R", "H", "H", "R", "R", "H"], self.doc["abab"]["order"])

    def test_Baseline_ShouldDeclareTwoLaunchesPerInvocation(self):
        # The third series' design (baseline.README.md, "Before the flip"): one field, read by
        # run-perf-abab.sh for the in-job run only.
        self.assertEqual(2, self.doc["abab"]["launch_count"])
        self.assertEqual(1080, self.doc["abab"]["invocation_timeout_s"])

    def test_Workflow_ShouldNotPassALaunchCountToTheAbsoluteGate(self):
        # The absolute bands were calibrated on the sources' own job; only the in-job run doubles.
        absolute = self.code[:self.code.index("- name: Read the declared in-job reference")]
        self.assertNotRegex(absolute, r"(?i)launch")

    def test_Baseline_ShouldShipOnlyTheBimodalRowOverride_WhileTheRunIsAnAATest(self):
        # The second series names one override before it runs, and only that one: any other is a
        # change after seeing the runs and starts a new series (baseline.README.md).
        rows = {k: v for k, v in self.doc["abab"]["rows"].items() if not k.startswith("_")}
        name = "Verbara.Sdk.Benchmarks.AsyncEventPumpBenchmark.EnqueueAndConsume1000Events"
        self.assertEqual([name], list(rows))
        self.assertIs(False, rows[name]["judge_time"])
        self.assertEqual({"judge_time", "reason"}, set(rows[name]))
        self.assertTrue(rows[name]["reason"].strip())
        self.assertEqual({}, self.doc["abab"]["accepted"])

    def test_Baseline_ShouldStillLoadUnderTheAbsoluteComparator(self):
        empty = tempfile.mkdtemp()
        try:
            environ = dict(os.environ)
            environ.pop("GITHUB_OUTPUT", None)
            result = subprocess.run([sys.executable, _ABSOLUTE, "--baseline", _REAL_BASELINE,
                                     "--artifacts-root", empty, "--enforce"],
                                    capture_output=True, text=True, check=False, env=environ)
        finally:
            shutil.rmtree(empty, ignore_errors=True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)

    def test_Workflow_ShouldDeclareTheAbabSwitchOnce(self):
        self.assertEqual(1, len(re.findall(r"(?m)^\s+PERF_ABAB_ENFORCE:", self.code)))

    def test_Workflow_ShouldEnforceTheAbabComparison(self):
        # Flipped together with the workflow line, after the third series met its prediction.
        self.assertRegex(self.code, r"(?m)^\s+PERF_ABAB_ENFORCE: 'true'$")
        # The absolute gate keeps enforcing; the two switches are independent.
        self.assertRegex(self.code, r"(?m)^\s+PERF_GATE_ENFORCE: 'true'$")

    def test_Workflow_ShouldTieTheCompareStepToTheSwitch(self):
        step = _step(self.code, "Compare HEAD against the reference in this job")
        self.assertIn("continue-on-error: ${{ env.PERF_ABAB_ENFORCE != 'true' }}", step)
        self.assertIn("scripts/ci/check-perf-abab.py", step)
        self.assertNotIn("--enforce", step)
        read = _step(self.code, "Read the declared in-job reference")
        self.assertIn("continue-on-error: ${{ env.PERF_ABAB_ENFORCE != 'true' }}", read)

    def test_Workflow_ShouldRunEveryAbabStepUnlessCancelled(self):
        for name in _ABAB_STEPS:
            with self.subTest(step=name):
                self.assertIn("if: ${{ !cancelled() }}", _step(self.code, name))
        run = _step(self.code, "Run the reference and HEAD alternated (R H H R R H)")
        self.assertIn("continue-on-error: true", run)
        self.assertRegex(run, r"timeout-minutes: \d+")
        self.assertIn("scripts/ci/run-perf-abab.sh", run)

    def test_Workflow_ShouldPlaceTheAbabStepsAfterTheAbsoluteGate(self):
        positions = [self.code.index(f"- name: {name}") for name in _ABAB_STEPS]
        self.assertEqual(sorted(positions), positions)
        self.assertLess(self.code.index("- name: File or update the breach issue"), positions[0])
        self.assertLess(positions[-1], self.code.index("- name: Upload BenchmarkDotNet JSON"))

    def test_Workflow_ShouldLeaveRoomForTheDoubledRun(self):
        run = _step(self.code, "Run the reference and HEAD alternated (R H H R R H)")
        step = int(re.search(r"timeout-minutes: (\d+)", run).group(1))
        abab = self.doc["abab"]
        invocations = len(abab["order"])
        # run-perf-abab.sh gives every `timeout` call a 30 s --kill-after grace and the lists 120 s.
        grace, lists = 30, 120
        self.assertGreaterEqual(step * 60, invocations * (abab["invocation_timeout_s"] + grace)
                                + 2 * (lists + grace))

    def test_Workflow_ShouldNeverLetTheJobTimeoutFire_WhenEveryStepRunsToItsOwnTimeout(self):
        # A cancelled job skips every `!cancelled()` comparison step, so the job's timeout must
        # outlast every step at its own timeout: each step declares one, and their sum is less
        # than the job's.
        job = int(re.search(r"(?m)^    timeout-minutes: (\d+)$", self.code).group(1))
        steps = self.code.split("\n    steps:\n", 1)[1]
        bodies = re.split(r"(?m)^      - ", steps)[1:]
        total = 0
        for body in bodies:
            label = body.splitlines()[0]
            with self.subTest(step=label):
                found = re.search(r"(?m)^        timeout-minutes: (\d+)$", body)
                self.assertIsNotNone(found, f"step {label!r} declares no timeout-minutes")
                if found:
                    total += int(found.group(1))
        self.assertGreater(len(bodies), 20)
        self.assertLess(total, job)

    def test_Workflow_ShouldUploadTheAbabArtifacts(self):
        upload = _step(self.code, "Upload BenchmarkDotNet JSON reports")
        for path in ("artifacts-abab/manifest.json", "artifacts-abab/list-*.txt",
                     "artifacts-abab/*/results/*-report-full-compressed.json",
                     "artifacts-abab/*/*.log", "perf-abab-report.md", "perf-abab.json"):
            self.assertIn(path, upload)

    def test_Workflow_ShouldNotFileTheBreachIssueFromTheAbabResult(self):
        issue = _step(self.code, "File or update the breach issue")
        self.assertNotIn("abab", issue)

    def test_Workflow_ShouldBuildTheReferenceWithTheSameCommandAsHead(self):
        head = _step(self.code, "Build benchmarks (Release)")
        ref = _step(self.code, "Build the reference benchmarks (Release)")
        command = "dotnet build Tests/Verbara.Sdk.Benchmarks/ -c Release --nologo"
        self.assertIn(command, head)
        self.assertIn(command, ref)
        self.assertIn("perf-ref", ref)


if __name__ == "__main__":
    unittest.main()
