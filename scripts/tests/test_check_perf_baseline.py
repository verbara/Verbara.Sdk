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
_PUBLISH = os.path.join(_REPO, ".github", "workflows", "publish.yml")

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


def _entry(full_name, mean=1000.0, stats="ok", allocated=None, memory=True):
    entry = {
        "Namespace": "Verbara.Sdk.Benchmarks",
        "Type": "FakeBenchmark",
        "Method": full_name.split("(", 1)[0].rsplit(".", 1)[1],
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
    elif stats == "nan-mean":
        entry["Statistics"] = {"Mean": float("nan")}
    elif stats == "inf-mean":
        entry["Statistics"] = {"Mean": float("inf")}
    elif stats == "null":
        entry["Statistics"] = None
    elif stats == "absent":
        pass
    if memory:
        entry["Memory"] = {"Gen0Collections": 0, "Gen1Collections": 0, "Gen2Collections": 0,
                           "TotalOperations": 1000, "BytesAllocatedPerOperation": allocated}
        if allocated is None:
            entry["Memory"]["BytesAllocatedPerOperation"] = 0
    return entry


_GAMMA = f"{_TYPE}.Gamma"

# Bytes decide, time informs: Alpha is gated on time AND exact bytes, Gamma on bytes only within a bound.
_BYTES_BASELINE = {
    "sources": {"fake": _TYPE},
    "jit_isa": "x86-64-v3",
    "benchmarks": {
        _ALPHA: {"mean_ns": 1000.0, "tolerance_pct": 10, "bytes": 168, "bytes_tolerance": 0},
        _GAMMA: {"bytes": 525994, "bytes_tolerance": 1024},
    },
}


_ENVIRONMENT = {"dotnet_sdk": "10.0.404", "dotnet_runtime": "10.0.15", "logical_cores": 4}


def _bdn_log(isa):
    return ("// * Summary *\n\nBenchmarkDotNet v0.15.8, Linux\n"
            f"  [Host]   : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT {isa}\n"
            f"  ShortRun : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT {isa}\n")


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

    def _write_report(self, entries, subdir="fake", type_name=_TYPE, results=True, raw=None,
                      cores=None, host_extra=None):
        directory = os.path.join(self.root, subdir)
        if results:
            directory = os.path.join(directory, "results")
        os.makedirs(directory, exist_ok=True)
        path = os.path.join(directory, f"{type_name}-report-full-compressed.json")
        with open(path, "w", encoding="utf-8") as handle:
            if raw is not None:
                handle.write(raw)
            else:
                host = {"BenchmarkDotNetVersion": "0.15.8", "ProcessorName": "AMD EPYC 7763"}
                if cores is not None:
                    host["LogicalCoreCount"] = cores
                if host_extra:
                    host.update(host_extra)
                handle.write(json.dumps({
                    "Title": "FakeBenchmark",
                    "HostEnvironmentInfo": host,
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

    def test_Main_ShouldReportFasterWithoutFailing_WhenEnforcingAndOnlyFaster(self):
        # Reported in both directions, failing in one: an unexplained speed-up is a stale baseline
        # or a benchmark measuring nothing and wants a human, so it is still a breach and still
        # files the issue, but the hosted fleet reads the same code up to 2-3x apart and a
        # FASTER-only week must not red the gate.
        self._write_report([_entry(_ALPHA, 400.0), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("FASTER", result.stdout)
        self.assertIn("::warning::perf-baseline BREACH", result.stdout)
        self.assertNotIn("::error::", result.stdout)
        self.assertEqual("breach", self._outputs().get("status"))
        self.assertEqual("1", self._outputs().get("breaches"))
        self.assertEqual("0", self._outputs().get("failing"))

    def test_Main_ShouldFailOnlyOnTheSlowerRow_WhenSlowerAndFasterTogether(self):
        # Alpha is SLOWER (fails), Beta is FASTER (reported): one error, one warning, exit 1.
        self._write_report([_entry(_ALPHA, 1400.0), _entry(_BETA, 10.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        errors = [line for line in result.stdout.splitlines() if line.startswith("::error::")]
        warnings = [line for line in result.stdout.splitlines()
                    if line.startswith("::warning::perf-baseline BREACH")]
        self.assertEqual(1, len(errors), result.stdout)
        self.assertIn("SLOWER", errors[0])
        self.assertEqual(1, len(warnings), result.stdout)
        self.assertIn("FASTER", warnings[0])
        self.assertEqual("2", self._outputs().get("breaches"))
        self.assertEqual("1", self._outputs().get("failing"))

    def test_Main_ShouldSayWhichBreachesFail_WhenRenderingMarkdown(self):
        self._write_report([_entry(_ALPHA, 1400.0), _entry(_BETA, 10.0)])
        self._run("--enforce")
        with open(self.markdown, encoding="utf-8") as handle:
            body = handle.read()
        self.assertIn("Failing breaches (1)", body)
        self.assertIn("Reported, not failing (1)", body)

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

    def test_Main_ShouldBreach_WhenABaselinedEntryHasNoMean(self):
        self._write_report([_entry(_ALPHA, stats="no-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("Statistics.Mean", result.stdout)

    def test_Main_ShouldBreach_WhenABaselinedEntryHasNullStatistics(self):
        self._write_report([_entry(_ALPHA, stats="null"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("STRUCTURAL", result.stdout)
        self.assertIn(_ALPHA, result.stdout)

    def test_Main_ShouldBreach_WhenABaselinedEntryHasNoStatistics(self):
        self._write_report([_entry(_ALPHA, stats="absent"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("STRUCTURAL", result.stdout)
        self.assertIn(_ALPHA, result.stdout)

    def test_Main_ShouldBreach_WhenABaselinedMeanIsNaN(self):
        # json.load accepts NaN, and NaN compares False both ways, so without a finite check the
        # row reads "ok" and a run with no measurement passes.
        self._write_report([_entry(_ALPHA, stats="nan-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("not a positive", result.stdout)

    def test_Main_ShouldBreach_WhenABaselinedMeanIsNotNumeric(self):
        self._write_report([_entry(_ALPHA, stats="text-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("not a positive", result.stdout)

    def test_Main_ShouldBreach_WhenABaselinedMeanIsABoolean(self):
        # `True` is an int in Python; without the bool guard it would compare as 1 ns.
        self._write_report([_entry(_ALPHA, stats="bool-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("not a positive", result.stdout)

    def test_Main_ShouldBreach_WhenABaselinedMeanIsZero(self):
        # A banded row keeps the positivity rule; only a row with no band treats 0 as the floor.
        self._write_report([_entry(_ALPHA, stats="zero-mean"), _entry(_BETA, 50.0)])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("STRUCTURAL", result.stdout)
        self.assertIn(_ALPHA, result.stdout)

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
    def test_Main_ShouldNotFail_WhenAnUnbaselinedEntryCarriesAMeasurement(self):
        # Otherwise the PR that ADDS a benchmark is blocked by the band it cannot yet have.
        extra = _entry(f"{_TYPE}.Gamma", 7.0)
        self._write_report([_entry(_ALPHA, 1010.0), _entry(_BETA, 52.0), extra])
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("::notice::", result.stdout)
        self.assertIn("Gamma", result.stdout)

    # ---- an entry with no band must still have measured -----------------
    def _with_unbaselined_gamma(self, stats, mean=7.0):
        self._write_report([_entry(_ALPHA, 1010.0), _entry(_BETA, 52.0),
                            _entry(_GAMMA, mean, stats=stats)])

    def _assert_structural_on_gamma(self, result):
        self.assertEqual(1, result.returncode, result.stdout)
        structural = [line for line in result.stdout.splitlines()
                      if line.startswith("::error::perf-baseline STRUCTURAL:")]
        self.assertTrue(any(_GAMMA in line for line in structural), result.stdout)

    def test_Main_ShouldBreach_WhenAnUnbaselinedEntryHasNullStatistics(self):
        # BenchmarkDotNet writes "Statistics": null for a case that produced no measurement.
        self._with_unbaselined_gamma("null")
        self._assert_structural_on_gamma(self._run("--enforce"))

    def test_Main_ShouldBreach_WhenAnUnbaselinedEntryHasNoStatistics(self):
        self._with_unbaselined_gamma("absent")
        self._assert_structural_on_gamma(self._run("--enforce"))

    def test_Main_ShouldBreach_WhenAnUnbaselinedEntryHasNoMean(self):
        self._with_unbaselined_gamma("no-mean")
        self._assert_structural_on_gamma(self._run("--enforce"))

    def test_Main_ShouldBreach_WhenAnUnbaselinedEntryHasANonNumericMean(self):
        for stats in ("text-mean", "bool-mean", "inf-mean", "nan-mean"):
            with self.subTest(stats=stats):
                shutil.rmtree(self.root, ignore_errors=True)
                self._with_unbaselined_gamma(stats)
                self._assert_structural_on_gamma(self._run("--enforce"))

    def test_Main_ShouldReportTheIssue_WhenObservingAndAnUnbaselinedEntryMeasuredNothing(self):
        self._with_unbaselined_gamma("null")
        result = self._run()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("::warning::perf-baseline STRUCTURAL:", result.stdout)
        self.assertNotIn("::error::", result.stdout)
        self.assertEqual("breach", self._outputs().get("status"))
        self.assertEqual("1", self._outputs().get("structural"))

    def test_Main_ShouldNotBreach_WhenAnUnbaselinedMeanIsAtTheResolutionFloor(self):
        # BenchmarkDotNet clamps a measurement at zero once the overhead is subtracted: a mean of 0
        # is a measurement at the harness's floor (ZeroMeasurement), not a missing one.
        self._with_unbaselined_gamma("zero-mean")
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertNotIn("STRUCTURAL", result.stdout)
        self.assertIn(f"'{_GAMMA}'", result.stdout)
        self.assertEqual("pass", self._outputs().get("status"))

    # ---- the declared `unbanded` set -------------------------------------
    def _unbanded_baseline(self, unbanded, benchmarks=None):
        doc = {"sources": {"fake": _TYPE},
               "benchmarks": benchmarks or dict(_BASELINE["benchmarks"]),
               "unbanded": unbanded}
        self._write_baseline(doc)

    def test_Main_ShouldBreach_WhenADeclaredUnbandedEntryIsAbsent(self):
        cases = [f"{_TYPE}.Decide(Seconds: {n})" for n in (1, 2, 4, 8)]
        self._unbanded_baseline({name: {"reason": "band not calibrated yet"} for name in cases})
        self._write_report([_entry(_ALPHA, 1010.0), _entry(_BETA, 52.0)]
                           + [_entry(name, 7.0) for name in cases[:3]])
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode, result.stdout)
        structural = [line for line in result.stdout.splitlines() if "STRUCTURAL" in line]
        self.assertEqual(1, len(structural), result.stdout)
        self.assertIn(cases[3], structural[0])
        self.assertIn("absent from the parsed results", structural[0])

    def test_Main_ShouldBreach_WhenADeclaredUnbandedEntryHasNullStatistics(self):
        self._unbanded_baseline({_GAMMA: {"reason": "resolution floor"}})
        self._with_unbaselined_gamma("null")
        self._assert_structural_on_gamma(self._run("--enforce"))

    def test_Main_ShouldPass_WhenEveryDeclaredUnbandedEntryMeasured(self):
        self._unbanded_baseline({_GAMMA: {"reason": "resolution floor"}})
        self._with_unbaselined_gamma("ok")
        result = self._run("--enforce", markdown=True)
        self.assertEqual(0, result.returncode, result.stdout)
        with open(self.markdown, encoding="utf-8") as handle:
            body = handle.read()
        self.assertIn("Declared without a band (1)", body)
        self.assertIn(f"| `{_GAMMA}` | 7.00 |", body)
        self.assertNotIn("Measured but not baselined", body)

    def test_Main_ShouldExitTwo_WhenUnbandedIsNotAnObject(self):
        self._unbanded_baseline([_GAMMA])
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)
        self.assertIn("unbanded", result.stdout)

    def test_Main_ShouldExitTwo_WhenAnUnbandedEntryHasNoReason(self):
        for entry in ({}, {"reason": ""}, {"reason": 3}, "floor"):
            with self.subTest(entry=entry):
                self._unbanded_baseline({_GAMMA: entry})
                result = self._run()
                self.assertEqual(2, result.returncode, result.stdout)
                self.assertIn(_GAMMA, result.stdout)

    def test_Main_ShouldExitTwo_WhenAnUnbandedEntryBelongsToNoSource(self):
        self._unbanded_baseline({"Other.Type.Method": {"reason": "x"}})
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)
        self.assertIn("Other.Type.Method", result.stdout)

    def test_Main_ShouldExitTwo_WhenAKeyIsBothBandedAndUnbanded(self):
        self._unbanded_baseline({_ALPHA: {"reason": "x"}})
        result = self._run()
        self.assertEqual(2, result.returncode, result.stdout)
        self.assertIn(_ALPHA, result.stdout)

    def test_Main_ShouldLoad_WhenAParameterValueContainsADot(self):
        ratio = f"{_TYPE}.Alpha(Ratio: 0.5)"
        self._write_baseline({"sources": {"fake": _TYPE},
                              "benchmarks": {ratio: {"mean_ns": 1000.0, "tolerance_pct": 10}},
                              "unbanded": {f"{_TYPE}.Beta(Ratio: 2.5)": {"reason": "x"}}})
        self._write_report([_entry(ratio, 1010.0), _entry(f"{_TYPE}.Beta(Ratio: 2.5)", 7.0)])
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout)

    # ---- bytes decide, time informs -------------------------------------
    def _bytes_fixture(self, alpha_bytes=168, gamma_bytes=525994, isa="x86-64-v3", log=True,
                       alpha_memory=True, cores=None):
        self._write_baseline(_BYTES_BASELINE)
        self._write_report([_entry(_ALPHA, 1010.0, allocated=alpha_bytes, memory=alpha_memory),
                            _entry(_GAMMA, 287000.0, allocated=gamma_bytes)], cores=cores)
        if log:
            with open(os.path.join(self.root, "fake", "FakeBenchmark-20261029.log"), "w",
                      encoding="utf-8") as handle:
                handle.write(_bdn_log(isa))

    def test_Main_ShouldPass_WhenBytesAreExactAndInsideTheirBound(self):
        self._bytes_fixture(gamma_bytes=525994 + 1024)   # the bound is inclusive
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("inside their bands and bounds", result.stdout)

    def test_Main_ShouldBreach_WhenAnExactRowAllocatesOneByteMore(self):
        # The regression this column exists for: deterministic, and blind to runner and ISA.
        self._bytes_fixture(alpha_bytes=169)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("MORE-BYTES", result.stdout)
        self.assertIn("+1 B", result.stdout)

    def test_Main_ShouldBreach_WhenABoundedRowLeavesItsBound(self):
        self._bytes_fixture(gamma_bytes=769620)   # the pump's 24-byte slot: +50 %
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("MORE-BYTES", result.stdout)
        self.assertIn(_GAMMA, result.stdout)

    def test_Main_ShouldReportFewerBytesWithoutFailing_WhenEnforcing(self):
        # Reported like FASTER: fewer bytes is a stale baseline or work that went away, and wants a
        # human, but it is not the regression this gate fails on.
        self._bytes_fixture(alpha_bytes=96)
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("FEWER-BYTES", result.stdout)
        self.assertNotIn("::error::", result.stdout)
        self.assertEqual("breach", self._outputs().get("status"))
        self.assertEqual("0", self._outputs().get("failing"))

    def test_Main_ShouldFail_WhenEnforcingAndAFasterRowAlsoAllocatesMore(self):
        # One row, two verdicts: the FASTER time is reported, the MORE-BYTES still fails.
        self._write_baseline(_BYTES_BASELINE)
        self._write_report([_entry(_ALPHA, 400.0, allocated=169),
                            _entry(_GAMMA, 287000.0, allocated=525994)])
        with open(os.path.join(self.root, "fake", "x.log"), "w", encoding="utf-8") as handle:
            handle.write(_bdn_log("x86-64-v3"))
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("MORE-BYTES", result.stdout)
        self.assertEqual("2", self._outputs().get("breaches"))
        self.assertEqual("1", self._outputs().get("failing"))

    def test_Main_ShouldCountStructuralFailuresAsFailing_WhenEnforcing(self):
        # No results directory, and both baselined rows absent: three structural failures, all failing.
        result = self._run("--enforce", root=os.path.join(self._tmp, "nope"))
        self.assertEqual(1, result.returncode)
        self.assertEqual("0", self._outputs().get("breaches"))
        self.assertEqual("3", self._outputs().get("structural"))
        self.assertEqual("3", self._outputs().get("failing"))

    def test_Main_ShouldReportBytesBreachWithoutFailing_WhenObserving(self):
        self._bytes_fixture(alpha_bytes=656)
        result = self._run()
        self.assertEqual(0, result.returncode)
        self.assertEqual("breach", self._outputs().get("status"))
        self.assertIn("::warning::", result.stdout)

    def test_Main_ShouldNameTheCoreCount_WhenABytesRowBreaches(self):
        # A default ConcurrentDictionary sizes its locks by the core count, so a bytes breach is
        # the code only against a baseline read on the same count. The message used to say bytes
        # "do not depend on the runner" and "this is the code", which a 24-core baseline read on
        # the 4-core hosted runner disproved.
        self._bytes_fixture(alpha_bytes=169, cores=4)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("measured on 4 logical cores", result.stdout)
        self.assertIn("only if the baseline was read on the same core count", result.stdout)
        self.assertNotIn("do not depend on the runner", result.stdout)

    def test_Main_ShouldSayTheCoreCountIsUnrecorded_WhenTheReportOmitsIt(self):
        self._bytes_fixture(alpha_bytes=169)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("a logical core count the report does not record", result.stdout)

    def test_Main_ShouldPrintBytesInFull_WhenAValueHasSevenDigits(self):
        # `:g` printed 1046656 as 1.04666e+06 and a +8 B change disappeared in the rounding.
        self._bytes_fixture(gamma_bytes=1_046_656, cores=4)
        result = self._run()
        self.assertIn("allocates 1046656 B per operation", result.stdout)
        self.assertIn("(+520662 B)", result.stdout)
        self.assertNotIn("e+06", result.stdout)
        with open(self.markdown, encoding="utf-8") as handle:
            body = handle.read()
        self.assertIn("| 525994 | 1046656 | +520662 | ±1024 |", body)
        self.assertNotIn("e+06", body)

    def test_Main_ShouldBreach_WhenABytesRowCarriesNoMemoryDiagnoserOutput(self):
        # A removed [MemoryDiagnoser] would otherwise switch the bytes gate off in silence.
        self._bytes_fixture(alpha_memory=False)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("BytesAllocatedPerOperation", result.stdout)

    def test_Main_ShouldGateBytesOnly_WhenTheEntryHasNoTimeBand(self):
        # Gamma has no mean_ns: a time 100x off its (absent) band is not a breach.
        self._write_baseline(_BYTES_BASELINE)
        self._write_report([_entry(_ALPHA, 1010.0, allocated=168),
                            _entry(_GAMMA, 28_700_000.0, allocated=525994)])
        with open(os.path.join(self.root, "fake", "x.log"), "w", encoding="utf-8") as handle:
            handle.write(_bdn_log("x86-64-v3"))
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout)

    def test_Main_ShouldWriteBytesColumns_WhenRenderingMarkdown(self):
        self._bytes_fixture()
        self._run()
        with open(self.markdown, encoding="utf-8") as handle:
            body = handle.read()
        self.assertIn("observed (B)", body)
        self.assertIn("| 168 | 168 | +0 | ±0 |", body)

    def test_Main_ShouldListUnbaselinedBytes_WhenRenderingMarkdown(self):
        self._both_in_band()
        self._write_report([_entry(_ALPHA, 1010.0), _entry(_BETA, 52.0),
                            _entry(_GAMMA, 7.0, allocated=176)])
        self._run()
        with open(self.markdown, encoding="utf-8") as handle:
            body = handle.read()
        self.assertIn(f"| `{_GAMMA}` | 7.00 | 176 |", body)

    # ---- the ISA pin -----------------------------------------------------
    def test_Main_ShouldBreach_WhenTheJitTargetedAnotherIsa(self):
        # The pin fell off (or its variable was misspelled): the times are not comparable.
        self._bytes_fixture(isa="x86-64-v4")
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("ISA pin did not take", result.stdout)

    def test_Main_ShouldBreach_WhenNoLogConfirmsThePinnedIsa(self):
        self._bytes_fixture(log=False)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("cannot be confirmed", result.stdout)

    def test_Main_ShouldSkipTheIsaCheck_WhenTheBaselineDeclaresNoIsa(self):
        self._both_in_band()   # _BASELINE has no jit_isa and the fixture writes no log
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout)

    # ---- the pinned environment ------------------------------------------
    def _environment_fixture(self, sdk="10.0.404", runtime=".NET 10.0.15 (10.0.15, 10.0.1526.53008)",
                             cores=4, declared=None):
        doc = dict(_BASELINE, environment=declared or dict(_ENVIRONMENT))
        self._write_baseline(doc)
        host = {}
        if sdk is not None:
            host["DotNetCliVersion"] = sdk
        if runtime is not None:
            host["RuntimeVersion"] = runtime
        self._write_report([_entry(_ALPHA, 1010.0), _entry(_BETA, 52.0)], cores=cores,
                           host_extra=host)

    def test_Main_ShouldPass_WhenTheReportShowsTheDeclaredEnvironment(self):
        self._environment_fixture()
        result = self._run("--enforce")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("environment sdk=10.0.404 runtime=10.0.15 logical_cores=4", result.stdout)

    def test_Main_ShouldBreach_WhenTheDeclaredSdkDiffersFromTheObservedOne(self):
        # The negative control: the same report, a different declaration, and the gate is red.
        self._environment_fixture(declared=dict(_ENVIRONMENT, dotnet_sdk="10.0.403"))
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertIn("measured on SDK 10.0.404, not the declared 10.0.403", result.stdout)

    def test_Main_ShouldBreach_WhenTheRuntimeRolledForward(self):
        self._environment_fixture(runtime=".NET 10.0.16 (10.0.16, 10.0.1626.1)")
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertIn("measured on runtime 10.0.16, not the declared 10.0.15", result.stdout)

    def test_Main_ShouldBreach_WhenTheCoreCountDiffers(self):
        # Bytes move with the core count, so a run on another count is another experiment.
        self._environment_fixture(cores=8)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode, result.stdout)
        self.assertIn("measured on logical core count 8, not the declared 4", result.stdout)

    def test_Main_ShouldBreach_WhenTheReportRecordsNoEnvironment(self):
        self._environment_fixture(sdk=None, runtime="unparseable", cores=None)
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode, result.stdout)
        for field in ("DotNetCliVersion", "RuntimeVersion", "LogicalCoreCount"):
            self.assertIn(f"does not record a readable HostEnvironmentInfo.{field}", result.stdout)

    def test_Main_ShouldBreachWithoutFailing_WhenObservingAndTheEnvironmentDiffers(self):
        self._environment_fixture(sdk="10.0.405")
        result = self._run()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual("breach", self._outputs()["status"])
        self.assertEqual("1", self._outputs()["structural"])

    def test_Main_ShouldNotReportTheEnvironmentTwice_WhenTheReportIsMissing(self):
        doc = dict(_BASELINE, environment=dict(_ENVIRONMENT))
        self._write_baseline(doc)
        os.makedirs(os.path.join(self.root, "fake", "results"))
        result = self._run("--enforce")
        self.assertEqual(1, result.returncode)
        self.assertIn("is missing", result.stdout)
        self.assertNotIn("cannot be confirmed", result.stdout,
                         "a missing report is reported once, by the report check")

    def test_Main_ShouldExitTwo_WhenTheEnvironmentIsPartial(self):
        for key in _ENVIRONMENT:
            with self.subTest(missing=key):
                partial = {k: v for k, v in _ENVIRONMENT.items() if k != key}
                self._write_baseline(dict(_BASELINE, environment=partial))
                self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenTheEnvironmentNamesAVersionRange(self):
        for key, value in (("dotnet_sdk", "10.0.x"), ("dotnet_runtime", "10.0"),
                           ("dotnet_sdk", ""), ("logical_cores", 0), ("logical_cores", True),
                           ("logical_cores", "4")):
            with self.subTest(key=key, value=value):
                self._write_baseline(dict(_BASELINE, environment=dict(_ENVIRONMENT, **{key: value})))
                self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenTheEnvironmentHasAnUnknownKey(self):
        # A key nobody compares would read as pinned when it is not.
        self._write_baseline(dict(_BASELINE, environment=dict(_ENVIRONMENT, cpu="EPYC 7763")))
        self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenTheEnvironmentIsNotAnObject(self):
        self._write_baseline(dict(_BASELINE, environment="10.0.404"))
        self.assertEqual(2, self._run().returncode)

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

    def test_Main_ShouldExitTwo_WhenAnEntryGatesNothing(self):
        self._write_baseline({"sources": {"fake": _TYPE}, "benchmarks": {_ALPHA: {}}})
        self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenBytesAreNegative(self):
        self._write_baseline({"sources": {"fake": _TYPE},
                              "benchmarks": {_ALPHA: {"bytes": -1}}})
        self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenBytesToleranceIsNotANumber(self):
        self._write_baseline({"sources": {"fake": _TYPE},
                              "benchmarks": {_ALPHA: {"bytes": 168, "bytes_tolerance": "1 KB"}}})
        self.assertEqual(2, self._run().returncode)

    def test_Main_ShouldExitTwo_WhenJitIsaIsEmpty(self):
        doc = dict(_BASELINE, jit_isa="")
        self._write_baseline(doc)
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


_BENCHMARKS_DIR = os.path.dirname(_REAL_BASELINE)
_BENCHMARK_ATTRIBUTE = re.compile(r"^\s*\[Benchmark(?:\([^\]]*\))?\]\s*$")
_METHOD = re.compile(r"^\s*public\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]?,. ]+?\s+(\w+)\s*\(")


def _split_full_name(full_name):
    """(type, method) of a BenchmarkDotNet FullName, ignoring a `(Param: value)` suffix."""
    bare = re.sub(r"\([^()]*\)$", "", full_name)
    owner, method = bare.rsplit(".", 1)
    return owner, method


def _benchmark_methods(type_name):
    """The [Benchmark] methods the one benchmark source file declaring `type_name` carries.
    `[BenchmarkCategory]` and other attributes do not count; attributes between `[Benchmark]` and
    the method signature are skipped."""
    simple = type_name.rsplit(".", 1)[1]
    declaring = re.compile(rf"\bclass\s+{re.escape(simple)}\b")
    files = []
    for name in sorted(os.listdir(_BENCHMARKS_DIR)):
        if name.endswith(".cs"):
            with open(os.path.join(_BENCHMARKS_DIR, name), encoding="utf-8") as handle:
                text = handle.read()
            if declaring.search(text):
                files.append(text)
    if len(files) != 1:
        raise AssertionError(f"expected one file declaring class {simple}, found {len(files)}")
    methods = set()
    pending = False
    for line in files[0].splitlines():
        if _BENCHMARK_ATTRIBUTE.match(line):
            pending = True
            continue
        if not pending or line.strip().startswith("["):
            continue
        match = _METHOD.match(line)
        if match is None:
            raise AssertionError(f"{simple}: a [Benchmark] attribute is not followed by a public "
                                 f"method signature: {line.strip()!r}")
        methods.add(match.group(1))
        pending = False
    return methods


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
            owner, _method = _split_full_name(full_name)
            self.assertIn(owner, types, f"{full_name} has no sources row")
            self.assertTrue("mean_ns" in entry or "bytes" in entry, f"{full_name} gates nothing")
            if "mean_ns" in entry:
                self.assertIsInstance(entry["mean_ns"], (int, float))
                self.assertGreater(entry["mean_ns"], 0)
                self.assertIsInstance(entry["tolerance_pct"], (int, float))
                self.assertGreater(entry["tolerance_pct"], 0)
            if "bytes" in entry:
                self.assertIsInstance(entry["bytes"], int)
                self.assertGreaterEqual(entry["bytes"], 0)
                self.assertGreaterEqual(entry.get("bytes_tolerance", 0), 0)

    def test_Baseline_ShouldNameEveryBenchmarkMethodOfEachSource_BandedOrUnbanded(self):
        # The weekly gate can only notice a missing entry it expects. A benchmark method with no
        # `benchmarks` and no `unbanded` entry would be a notice forever, and an entry whose method
        # is gone would only surface a week later: both are caught here, on the pull request.
        sources = {k: v for k, v in self.doc["sources"].items() if not k.startswith("_")}
        named = {}
        for key in list(self.doc["benchmarks"]) + list(self.doc.get("unbanded", {})):
            if key.startswith("_"):
                continue
            owner, method = _split_full_name(key)
            named.setdefault(owner, set()).add(method)
        for type_name in sorted(set(sources.values())):
            with self.subTest(type=type_name):
                in_code = _benchmark_methods(type_name)
                in_baseline = named.get(type_name, set())
                missing = sorted(in_code - in_baseline)
                stale = sorted(in_baseline - in_code)
                self.assertFalse(missing, f"{type_name}: [Benchmark] method(s) {missing} have "
                                          f"neither a \"benchmarks\" nor an \"unbanded\" entry")
                self.assertFalse(stale, f"{type_name}: baseline.json names {stale}, which the "
                                        f"class does not declare as [Benchmark] methods")

    def test_Baseline_ShouldLoadUnderTheComparator(self):
        # Against an empty artifacts root every source is a structural failure (exit 1). Exit 2
        # would mean the committed baseline itself fails the comparator's own validation.
        empty = tempfile.mkdtemp()
        try:
            environ = dict(os.environ)
            environ.pop("GITHUB_OUTPUT", None)
            result = subprocess.run([sys.executable, _SCRIPT, "--baseline", _REAL_BASELINE,
                                     "--artifacts-root", empty, "--enforce"],
                                    capture_output=True, text=True, check=False, env=environ)
        finally:
            shutil.rmtree(empty, ignore_errors=True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)

    def test_Baseline_ShouldPinTheIsaTheWorkflowSets(self):
        # The comparator checks the logs against jit_isa; the workflow must set the variable that
        # really moves the JIT there. On .NET 10 `DOTNET_EnableAVX512F` is accepted and ignored.
        self.assertEqual("x86-64-v3", self.doc.get("jit_isa"))
        code = "\n".join(line for line in self.workflow.splitlines()
                         if not line.lstrip().startswith("#"))
        self.assertRegex(code, r"(?m)^\s+DOTNET_EnableAVX512: '0'$")
        self.assertNotRegex(code, r"DOTNET_EnableAVX512F")

    def test_Baseline_ShouldPinTheEnvironmentTheWorkflowInstalls(self):
        # The workflow installs the SDK baseline.json declares, alone, in an empty directory; a
        # floating `10.0.x` here would hand the job whatever the runner image carries.
        env = self.doc.get("environment")
        self.assertIsInstance(env, dict, "baseline.json must declare \"environment\"")
        self.assertRegex(env["dotnet_sdk"], r"^[0-9]+\.[0-9]+\.[0-9]+$")
        self.assertRegex(env["dotnet_runtime"], r"^[0-9]+\.[0-9]+\.[0-9]+$")
        self.assertEqual(4, env["logical_cores"], "every `bytes` here is the 4-core hosted figure")
        code = "\n".join(line for line in self.workflow.splitlines()
                         if not line.lstrip().startswith("#"))
        self.assertNotIn("'10.0.x'", code)
        setup = re.findall(r"(?m)^\s+- uses: actions/setup-dotnet@\S+\n((?:\s{8,}.*\n)+)", code)
        self.assertEqual(1, len(setup), "exactly one setup-dotnet step")
        self.assertIn("DOTNET_INSTALL_DIR:", setup[0])
        self.assertRegex(setup[0], r"dotnet-version: \$\{\{ steps\.declared\.outputs\.sdk \}\}")
        order = [code.index(marker) for marker in (
            "id: declared", "actions/setup-dotnet@",
            "name: Confirm the pinned SDK and runtime are the only ones reachable",
            "name: Build benchmarks (Release)")]
        self.assertEqual(sorted(order), order, "declare, install, confirm, then build")

    def test_Publish_ShouldRecordTheSdkAndRuntimeBeforeBuilding(self):
        with open(_PUBLISH, encoding="utf-8") as handle:
            code = "\n".join(line for line in handle.read().splitlines()
                             if not line.lstrip().startswith("#"))
        record = code.find("dotnet --version\n          dotnet --info")
        self.assertGreaterEqual(record, 0, "publish.yml must print dotnet --version and --info")
        first_build = re.search(r"\bdotnet (build|pack|restore)\b", code)
        self.assertIsNotNone(first_build)
        self.assertLess(record, first_build.start(), "printed before anything is built")
        self.assertLess(code.index("actions/setup-dotnet@"), record)

    def test_Baseline_ShouldGateTheEventPumpOnBytesButNotItsBimodalRow(self):
        pump = "Verbara.Sdk.Benchmarks.AsyncEventPumpBenchmark"
        for row in ("EnqueueOnly10000Events", "Prefill10000ThenConsume"):
            self.assertIn("bytes", self.doc["benchmarks"][f"{pump}.{row}"])
        self.assertNotIn(f"{pump}.EnqueueAndConsume1000Events", self.doc["benchmarks"])

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

    def test_Workflow_ShouldEnforceTheGate(self):
        # The flip landed: a commented-out or reverted line would put the gate back to observing.
        code = "\n".join(line for line in self.workflow.splitlines()
                         if not line.lstrip().startswith("#"))
        self.assertRegex(code, r"(?m)^\s+PERF_GATE_ENFORCE: 'true'$")
        self.assertNotRegex(code, r"PERF_GATE_ENFORCE: 'false'")

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
