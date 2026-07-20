"""Unit tests for check-exclusion-baseline.py (the denominator guard).

Builds tmp source trees + baseline fixtures and runs the script as a subprocess.
Stdlib unittest only — NO pip deps (ADR-0013).
"""
import json
import os
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-exclusion-baseline.py")

_MARKER = "[ExcludeFromCodeCoverage]\n"


class CheckExclusionBaselineTests(unittest.TestCase):
    def setUp(self):
        self._root = tempfile.mkdtemp()
        os.makedirs(os.path.join(self._root, "src", "pkg"))

    def tearDown(self):
        for base, _dirs, files in os.walk(self._root, topdown=False):
            for name in files:
                os.unlink(os.path.join(base, name))
            os.rmdir(base)

    def _write_cs(self, rel, marker_count=0, files_prefix="f"):
        path = os.path.join(self._root, "src", "pkg", rel)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("class C {}\n" + _MARKER * marker_count)
        return path

    def _make_source_files(self, n, markers_each=0):
        for i in range(n):
            self._write_cs(f"File{i}.cs", marker_count=markers_each)

    def _baseline(self, count, min_scanned=5, exclude_globs=None):
        doc = {
            "source_glob": "src/**/*.cs",
            "count": count,
            "min_scanned_files": min_scanned,
        }
        if exclude_globs is not None:
            doc["exclude_globs"] = exclude_globs
        path = os.path.join(self._root, "coverage-exclusion-baseline.json")
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(doc, handle)
        return path

    def _run(self, baseline_path):
        return subprocess.run(
            [sys.executable, _SCRIPT, baseline_path, self._root],
            capture_output=True, text=True,
        )

    def test_ShouldFail_WhenOverBaseline(self):
        # 10 files, 2 markers total; baseline 0 -> net-new 2 -> FAIL
        self._make_source_files(8, markers_each=0)
        self._write_cs("Excl0.cs", marker_count=1)
        self._write_cs("Excl1.cs", marker_count=1)
        result = self._run(self._baseline(count=0, min_scanned=5))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("exceed the committed baseline", result.stdout)
        self.assertIn("net-new 2", result.stdout)

    def test_ShouldPass_WhenAtBaseline(self):
        # 10 files, 3 markers total; baseline 3 -> equal -> PASS
        self._make_source_files(7, markers_each=0)
        self._write_cs("A.cs", marker_count=1)
        self._write_cs("B.cs", marker_count=1)
        self._write_cs("C.cs", marker_count=1)
        result = self._run(self._baseline(count=3, min_scanned=5))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Exclusion baseline OK.", result.stdout)

    def test_ShouldPass_WhenUnderBaseline(self):
        # 6 files, 1 marker; baseline 5 -> under -> PASS with tighten note
        self._make_source_files(5, markers_each=0)
        self._write_cs("A.cs", marker_count=1)
        result = self._run(self._baseline(count=5, min_scanned=5))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Exclusion baseline OK.", result.stdout)
        self.assertIn("below baseline", result.stdout)

    def test_ShouldFail_WhenTooFewFilesScanned(self):
        # only 2 files but min_scanned 5 -> liveness trip
        self._make_source_files(2, markers_each=0)
        result = self._run(self._baseline(count=0, min_scanned=5))
        self.assertEqual(result.returncode, 1)
        self.assertIn("mis-wired", result.stdout)

    def test_ShouldFail_WhenZeroFilesScanned(self):
        # no .cs files at all -> scanned 0 < min -> liveness trip
        result = self._run(self._baseline(count=0, min_scanned=1))
        self.assertEqual(result.returncode, 1)
        self.assertIn("scanned only 0 files", result.stdout)

    def test_ShouldExcludeBuildOutput_WhenExcludeGlobsSet(self):
        # a marker under obj/ must NOT count when excluded
        self._make_source_files(6, markers_each=0)
        os.makedirs(os.path.join(self._root, "src", "pkg", "obj"))
        with open(os.path.join(self._root, "src", "pkg", "obj", "Gen.cs"),
                  "w", encoding="utf-8") as handle:
            handle.write("class G {}\n" + _MARKER)
        result = self._run(self._baseline(
            count=0, min_scanned=5, exclude_globs=["src/**/obj/**"]))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Exclusion baseline OK.", result.stdout)


if __name__ == "__main__":
    unittest.main()
