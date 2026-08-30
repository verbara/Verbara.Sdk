"""Unit tests for scripts/ci/check-perf-baseline.py (the perf-regression gate, ADR-0042 D4).

The script guards a claim that only fails when nobody is looking — a weekly scheduled run — so it
needs its own guard, the `classify-docs-only.sh` / release-hygiene precedent. Runs the script as a
subprocess against synthetic BenchmarkDotNet report trees and asserts exit codes, the observing/
enforcing split, and every one of the five fail-closed conditions.

Also pins three CONTRACTS that live in two files at once and would otherwise drift silently:
the committed baseline's shape, its agreement with the workflow's `--artifacts` directories, and
the workflow's trigger set (§2.6 — no `pull_request`, no `merge_group`, so no required check).

Stdlib unittest only — NO pip deps (ADR-0013). Discovered by `python3 -m unittest discover
scripts/tests` in the always-on, required `Coverage Script Tests` job.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_HERE, os.pardir, os.pardir))
_SCRIPT = os.path.join(_REPO, "scripts", "ci", "check-perf-baseline.py")
_REAL_BASELINE = os.path.join(_REPO, "Tests", "Verbara.Sdk.Benchmarks", "baseline.json")
_WORKFLOW = os.path.join(_REPO, ".github", "workflows", "perf-regression.yml")

_TYPE = "Verbara.Sdk.Benchmarks.FakeBenchmark"
_ALPHA = f"{_TYPE}.Alpha"
_BETA = f"{_TYPE}.Beta"

# A baseline with a tight band (10%) and a loose one (50%), so one fixture can exercise both sides.
_BASELINE = {
    "sources": {"_comment": "ignored", "fake": _TYPE},
    "benchmarks": {
        _ALPHA: {"mean_ns": 1000.0, "tolerance_pct": 10},
        _BETA: {"mean_ns": 50.0, "tolerance_pct": 50},
    },
}


def _entry(full_name, mean=1000.0, stats="ok"):
    entry = {
        "Namespace": "Verbara.Sdk.Benchmarks",
        "Type": "FakeBenchmark",
        "Method": full_name.rsplit(".", 1)[1],
        "Parameters": "",
        "FullName": full_name,
    }
    if stats == "ok":
        entry["Statistics"] = {"N": 15, "Min": mean, "Mean": mean, "Max": mean,
                               "StandardDeviation": mean * 0.003}
    elif stats == "no-mean":
        entry["Statistics"] = {"N": 15, "Min": mean, "Max": mean}
    elif stats == "text-mean":
        entry["Statistics"] = {"Mean": "1000 ns"}
    elif stats == "bool-mean":
        entry["Statistics"] = {"Mean": True}
    elif stats == "zero-mean":
        entry["Statistics"] = {"Mean": 0}
    elif stats == "null":
        entry["Statistics"] = None
    elif stats == "absent":
        pass
    return entry


class CheckPerfBaselineTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.mkdtemp()
        self.baseline_path = os.path.join(self._tmp, "baseline.json")
        self._write_baseline(_BASELINE)
        self.root = os.path.join(self._tmp, "artifacts")
        self.markdown = os.path.join(self._tmp, "report.md")
        self.gh_output = os.path.join(self._tmp, "gh_output.txt")

    def tearDown(self):
        shutil.rmtree(self._tmp, ignore_errors=True)

    # ---- fixture helpers -------------------------------------------------
    def _write_baseline(self, doc):
        with open(self.baseline_path, "w", encoding="utf-8") as handle:
            json.dump(doc, handle)

    def _write_report(self, entries, subdir="fake", type_name=_TYPE, results=True, raw=None):
        directory = os.path.join(self.root, subdir)
        if results:
            directory = os.path.join(directory, "results")
        os.makedirs(directory, exist_ok=True)
        path = os.path.join(directory, f"{type_name}-report-full-compressed.json")
        with open(path, "w", encoding="utf-8") as handle:
            if raw is not None:
                handle.write(raw)
            else:
                handle.write(json.dumps({
                    "Title": "FakeBenchmark",
                    "HostEnvironmentInfo": {"BenchmarkDotNetVersion": "0.15.8",
                                            "ProcessorName": "AMD EPYC 7763"},
                    "Benchmarks": entries,
                }))
        return path

    def _both_in_band(self):
        self._write_report([_entry(_ALPHA, 1010.0), _entry(_BETA, 52.0)])

    def _run(self, *extra, baseline=None, root=None, markdown=True, env=None):
        cmd = [sys.executable, _SCRIPT,
               "--baseline", baseline or self.baseline_path,
               "--artifacts-root", root or self.root]
        if markdown:
            cmd += ["--markdown-out", self.markdown]
        cmd += list(extra)
        environ = dict(os.environ, GITHUB_OUTPUT=self.gh_output)
        environ.pop("PERF_GATE_ENFORCE", None)
        if env:
            environ.update(env)
        return subprocess.run(cmd, capture_output=True, text=True, check=False, env=environ)

    def _outputs(self):
        if not os.path.exists(self.gh_output):
            return {}
        with open(self.gh_output, encoding="utf-8") as handle:
            return dict(line.strip().split("=", 1) for line in handle if "=" in line)

    # ---- the happy path --------------------------------------------------
    def test_Main_ShouldPass_WhenEveryBenchmarkIsInsideItsBand(self):
        self._both_in_band()
        result = self._run()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("perf-baseline: OK", result.stdout)
        self.assertEqual("pass", self._outputs().get("status"))
        self.assertEqual("0", self._outputs().get("breaches"))
        self.assertEqual("false", self._outputs().get("enforcing"))

    def test_Main_ShouldPass_WhenDeviationSitsExactlyOnTheBandEdge(self):
        # 1100 ns is exactly +10.0% of a 1000 ns baseline with a 10% band: inclusive, not a breach.
        self._write_report([_entry(_ALPHA, 1100.0), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_Main_ShouldWriteMarkdown_WhenAsked(self):
        self._both_in_band()
        self._run()
        with open(self.markdown, encoding="utf-8") as handle:
            body = handle.read()
        self.assertIn("Perf regression vs", body)
        self.assertIn("OBSERVING", body)
        self.assertIn("FakeBenchmark.Alpha", body)

    # ---- band breaches ---------------------------------------------------
    def test_Main_ShouldReportSlowerBreach_WhenMeanExceedsTheBand(self):
        self._write_report([_entry(_ALPHA, 1400.0), _entry(_BETA, 50.0)])
        result = self._run()
        self.assertEqual(0, result.returncode, "observing mode must not fail the job (§2.5)")
        self.assertIn("SLOWER", result.stdout)
        self.assertIn("::warning::", result.stdout)
        self.assertNotIn("::error::", result.stdout)
        self.assertEqual("breach", self._outputs().get("status"))
        self.assertEqual("1", self._outputs().get("breaches"))

    def test_Main_ShouldFail_WhenSlowerBreachAndEnforcing(self):
        self._write_report([_entry(_ALPHA, 1400.0), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("::error::", result.stdout)
        self.assertEqual("true", self._outputs().get("enforcing"))

    def test_Main_ShouldEnforce_WhenEnvVarIsSetInsteadOfTheFlag(self):
        # The workflow flips ONE job-level env line; the flag is the local-debug equivalent.
        self._write_report([_entry(_ALPHA, 1400.0), _entry(_BETA, 50.0)])
        result = self._run(env={"PERF_GATE_ENFORCE": "true"})
        self.assertEqual(1, result.returncode)

    def test_Main_ShouldReportFasterBreach_WhenBenchmarkIsFarBelowBaseline(self):
        # Two-sided: an unexplained speed-up is a stale baseline or a benchmark measuring nothing.
        self._write_report([_entry(_ALPHA, 400.0), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("FASTER", result.stdout)

    # ---- fail-closed: the five structural conditions ---------------------
    def test_Main_ShouldBreach_WhenResultsDirectoryIsAbsent(self):
        os.makedirs(self.root, exist_ok=True)   # root exists, the class's results/ does not
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("results directory", result.stdout)

    def test_Main_ShouldBreach_WhenArtifactsRootIsEntirelyAbsent(self):
        result = self._run("--enforce", root=os.path.join(self._tmp, "nope"))
        self.assertEqual(1, result.returncode)
        self.assertIn("STRUCTURAL", result.stdout)

    def test_Main_ShouldBreach_WhenReportFileIsAbsent(self):
        os.makedirs(os.path.join(self.root, "fake", "results"), exist_ok=True)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("is missing", result.stdout)

    def test_Main_ShouldBreach_WhenReportIsUnparseable(self):
        self._write_report(None, raw="{not json")
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("unparseable", result.stdout)

    def test_Main_ShouldBreach_WhenBenchmarksArrayIsEmpty(self):
        self._write_report([])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("measured nothing", result.stdout)

    def test_Main_ShouldBreach_WhenBaselinedBenchmarkIsAbsentFromResults(self):
        self._write_report([_entry(_BETA, 50.0)])       # Alpha never ran
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("absent from the parsed results", result.stdout)

    def test_Main_ShouldBreach_WhenStatisticsMeanIsMissing(self):
        self._write_report([_entry(_ALPHA, stats="no-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("Statistics.Mean", result.stdout)

    def test_Main_ShouldBreach_WhenStatisticsIsNull(self):
        self._write_report([_entry(_ALPHA, stats="null"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)

    def test_Main_ShouldBreach_WhenStatisticsIsAbsent(self):
        self._write_report([_entry(_ALPHA, stats="absent"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)

    def test_Main_ShouldBreach_WhenStatisticsMeanIsNotNumeric(self):
        self._write_report([_entry(_ALPHA, stats="text-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("not a positive", result.stdout)

    def test_Main_ShouldBreach_WhenStatisticsMeanIsABoolean(self):
        # `True` is an int in Python; without the bool guard it would compare as 1 ns.
        self._write_report([_entry(_ALPHA, stats="bool-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("not a positive", result.stdout)

    def test_Main_ShouldBreach_WhenStatisticsMeanIsZero(self):
        self._write_report([_entry(_ALPHA, stats="zero-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)

    def test_Main_ShouldBreach_WhenAReportEntryHasNoFullName(self):
        broken = _entry(_ALPHA, 1010.0)
        del broken["FullName"]
        self._write_report([broken, _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("FullName", result.stdout)

    def test_Main_ShouldStillFailClosed_WhenObservingButStructurallyBroken(self):
        # Observing mode reports rather than fails, but the STATUS must still say breach so the
        # notification step fires — while the job is green, the issue is the only signal.
        result = self._run(root=os.path.join(self._tmp, "nope"))
        self.assertEqual(0, result.returncode)
        self.assertEqual("breach", self._outputs().get("status"))
        self.assertIn("OBSERVING mode", result.stdout)

    # ---- tolerant of what should be tolerated ----------------------------
    def test_Main_ShouldNotFail_WhenAMeasuredBenchmarkHasNoBaselineEntry(self):
        # Otherwise the PR that ADDS a benchmark is blocked by the band it cannot yet have.
        extra = _entry(f"{_TYPE}.Gamma", 7.0)
        self._write_report([_entry(_ALPHA, 1010.0), _entry(_BETA, 52.0), extra])
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("::notice::", result.stdout)
        self.assertIn("Gamma", result.stdout)

    # ---- exit 2: the guard itself is broken ------------------------------
    def test_Main_ShouldExitTwo_WhenBaselineIsMissing(self):
        result = self._run("--enforce", baseline=os.path.join(self._tmp, "nope.json"))
        self.assertEqual(2, result.returncode)

    def test_Main_ShouldExitTwo_WhenBaselineIsMalformed(self):
        with open(self.baseline_path, "w", encoding="utf-8") as handle:
            handle.write("{oops")
        result = self._run()
        self.assertEqual(2, result.returncode, "a broken guard must not read green in EITHER mode")

    def test_Main_ShouldExitTwo_WhenSourcesIsEmpty(self):
        self._write_baseline({"sources": {"_comment": "x"}, "benchmarks": _BASELINE["benchmarks"]})
        self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenBenchmarksIsEmpty(self):
        self._write_baseline({"sources": {"fake": _TYPE}, "benchmarks": {}})
        self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenAnEntryHasNoNumericMean(self):
        self._write_baseline({"sources": {"fake": _TYPE},
                              "benchmarks": {_ALPHA: {"mean_ns": "fast", "tolerance_pct": 10}}})
        self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenAnEntryHasNoTolerance(self):
        self._write_baseline({"sources": {"fake": _TYPE},
                              "benchmarks": {_ALPHA: {"mean_ns": 1000.0}}})
        self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenABaselinedTypeHasNoSourcesRow(self):
        # Its report would never be looked for, so its absence would go unnoticed — the `|| true`
        # hole in a different shape.
        self._write_baseline({
            "sources": {"fake": _TYPE},
            "benchmarks": {_ALPHA: {"mean_ns": 1000.0, "tolerance_pct": 10},
                           "Other.Type.Method": {"mean_ns": 1.0, "tolerance_pct": 10}},
        })
        result = self._run()
        self.assertEqual(2, result.returncode)
        self.assertIn("sources", result.stdout)

    def test_Main_ShouldExitTwo_WhenArgumentsAreMissing(self):
        result = subprocess.run([sys.executable, _SCRIPT], capture_output=True, text=True,
                                check=False)
        self.assertEqual(2, result.returncode)
        self.assertIn("usage", result.stdout)

    def test_Main_ShouldExitTwo_WhenAnArgumentIsUnrecognized(self):
        result = self._run("--rebaseline")
        self.assertEqual(2, result.returncode)


class CommittedBaselineContractTests(unittest.TestCase):
    """Pins the SHIPPED baseline.json and the workflow it is read by. These two files have to
    agree and live in different languages, which is exactly the drift nobody notices."""

    @classmethod
    def setUpClass(cls):
        with open(_REAL_BASELINE, encoding="utf-8") as handle:
            cls.doc = json.load(handle)
        with open(_WORKFLOW, encoding="utf-8") as handle:
            cls.workflow = handle.read()

    def test_Baseline_ShouldParseAndDeclareEveryBenchmarksType(self):
        sources = {k: v for k, v in self.doc["sources"].items() if not k.startswith("_")}
        types = set(sources.values())
        self.assertTrue(self.doc["benchmarks"], "baseline must not be empty")
        for full_name, entry in self.doc["benchmarks"].items():
            owner = full_name.rsplit(".", 1)[0]
            self.assertIn(owner, types, f"{full_name} has no sources row")
            self.assertIsInstance(entry["mean_ns"], (int, float))
            self.assertGreater(entry["mean_ns"], 0)
            self.assertIsInstance(entry["tolerance_pct"], (int, float))
            self.assertGreater(entry["tolerance_pct"], 0)

    def test_Baseline_ShouldRecordHowItWasCalibrated(self):
        # ADR-0042 D6: a baseline whose provenance is not written down cannot be re-derived, and a
        # number nobody can re-derive is the thing this whole change exists to stop shipping.
        calibration = self.doc["calibration"]
        for key in ("runs", "window_start", "window_end", "runner", "processors_observed",
                    "pooled_across_processors"):
            self.assertIn(key, calibration)
        self.assertGreaterEqual(calibration["runs"], 2)
        self.assertGreaterEqual(len(calibration["processors_observed"]), 2,
                                "ubuntu-latest alternates CPUs; the pooling is the whole point")

    def test_Baseline_ShouldMatchTheWorkflowArtifactDirectories(self):
        declared = {k for k in self.doc["sources"] if not k.startswith("_")}
        used = set(re.findall(r"--artifacts \.\./\.\./artifacts/(\S+)", self.workflow))
        self.assertEqual(declared, used,
                         "every `--filter` step needs a baseline `sources` row and vice versa")

    def test_Workflow_ShouldCallTheGuardAndTheNotifier(self):
        self.assertIn("scripts/ci/check-perf-baseline.py", self.workflow)
        self.assertIn("scripts/ci/report-perf-breach.sh", self.workflow)
        self.assertIn("PERF_GATE_ENFORCE", self.workflow)

    def test_Workflow_ShouldCarryNoPullRequestOrMergeGroupTrigger(self):
        # §2.6 / ADR-0042 D2-D3: no PR-visible check-run name => it can never become a required
        # context => no branch-protection reconciliation (verbara-meta/ADR-0003). Asserted rather
        # than merely verified once, because "someone added a trigger" is a silent change.
        code = "\n".join(line for line in self.workflow.splitlines()
                         if not line.lstrip().startswith("#"))
        triggers = re.search(r"^on:\n((?:[ \t].*\n|\n)*)", code, re.MULTILINE)
        self.assertIsNotNone(triggers, "workflow must declare an `on:` block")
        block = triggers.group(1)
        self.assertNotIn("pull_request", block)
        self.assertNotIn("merge_group", block)
        self.assertIn("schedule", block)
        self.assertIn("workflow_dispatch", block)

    def test_Docs_ShouldCarryTheBaselineUpdateProtocol(self):
        # ADR-0042 D6 — the protocol is a deliverable, not a habit.
        path = os.path.join(os.path.dirname(_REAL_BASELINE), "baseline.README.md")
        self.assertTrue(os.path.isfile(path), "baseline.README.md must sit next to baseline.json")
        with open(path, encoding="utf-8") as handle:
            body = handle.read()
        self.assertIn("CI never writes back", body)


if __name__ == "__main__":
    unittest.main()
