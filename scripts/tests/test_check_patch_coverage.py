"""Unit tests for check-patch-coverage.py (the diff-coverage primary gate).

Each test builds a throwaway git repo (a local "origin/main" ref + a feature
HEAD with committed changes), a fixture Cobertura report, and runs the script
with CWD inside the repo. Cases: below-patch FAIL, above PASS, shallow-clone
(unresolved merge-base) FAIL, touched-but-zero (mis-wired report) FAIL.

The four diff-cover-dependent cases are skipped when diff-cover is not installed
so `python3 -m unittest` still runs offline; CI installs the pinned diff-cover
(ADR-0013) so they always execute there. The shallow-clone case needs no
diff-cover and always runs. Stdlib unittest only — NO pip deps.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-patch-coverage.py")

_HAS_DIFF_COVER = shutil.which("diff-cover") is not None

_FLOOR = '{"line":78,"slack":3,"branch":64,"lines_valid_min":1,"patch":85}'


def _git(cwd, *args, check=True):
    return subprocess.run(
        ["git", *args], cwd=cwd, capture_output=True, text=True, check=check,
    )


def _cobertura(lines, filename="src/calc.py"):
    """lines: list of (number, hits). filename is repo-relative; the fixture emits
    it as an ABSOLUTE path under a matching <source> root, exactly as
    ReportGenerator's merged Cobertura does — so the script's path-normalization
    is genuinely exercised (a relative fixture would not test the real shape)."""
    line_xml = "".join(
        f'          <line number="{n}" hits="{h}"/>\n' for n, h in lines
    )
    covered = sum(1 for _, h in lines if h > 0)
    total = len(lines) or 1
    rate = covered / total
    return (
        f'<?xml version="1.0"?>\n'
        f'<coverage line-rate="{rate}" branch-rate="0" lines-valid="{total}" '
        f'lines-covered="{covered}" branches-valid="0" branches-covered="0" '
        f'version="1" timestamp="0">\n'
        f'  <sources><source>{{ROOT}}</source></sources>\n'
        f'  <packages><package name="calc" line-rate="{rate}" branch-rate="0" complexity="0">\n'
        f'    <classes><class name="calc" filename="{{ROOT}}/{filename}" line-rate="{rate}" '
        f'branch-rate="0" complexity="0"><methods/>\n'
        f'      <lines>\n{line_xml}      </lines>\n'
        f'    </class></classes>\n'
        f'  </package></packages>\n'
        f'</coverage>\n'
    )


class CheckPatchCoverageTests(unittest.TestCase):
    def setUp(self):
        self.repo = tempfile.mkdtemp()
        os.makedirs(os.path.join(self.repo, "src"))
        _git(self.repo, "init", "-q")
        _git(self.repo, "config", "user.email", "t@t.co")
        _git(self.repo, "config", "user.name", "t")
        _git(self.repo, "checkout", "-q", "-b", "main")
        self._write("src/calc.py",
                    "def add(a, b):\n    return a + b\n\n"
                    "def sub(a, b):\n    return a - b\n")
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "init")
        # A local ref named origin/main so `git merge-base origin/main HEAD` works
        # without a real remote (mirrors the CI merge-base target).
        _git(self.repo, "update-ref", "refs/remotes/origin/main", "HEAD")

    def tearDown(self):
        shutil.rmtree(self.repo, ignore_errors=True)

    def _write(self, rel, text):
        path = os.path.join(self.repo, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)

    def _commit_feature(self, calc_body):
        _git(self.repo, "checkout", "-q", "-b", "feature")
        self._write("src/calc.py", calc_body)
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "change")

    def _report(self, lines):
        text = _cobertura(lines).replace("{ROOT}", self.repo)
        path = os.path.join(self.repo, "Cobertura.xml")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)
        return path

    def _floor(self):
        path = os.path.join(self.repo, "coverage-floor.json")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(_FLOOR)
        return path

    def _run(self, report_path, floor_path):
        return subprocess.run(
            [sys.executable, _SCRIPT, report_path, floor_path],
            cwd=self.repo, capture_output=True, text=True,
        )

    @unittest.skipUnless(_HAS_DIFF_COVER, "diff-cover not installed")
    def test_ShouldPass_WhenPatchCoverageAboveFloor(self):
        # feature changes line 2 (add body) and line 5 (sub body); both COVERED
        self._commit_feature(
            "def add(a, b):\n    return a + b + 0\n\n"
            "def sub(a, b):\n    return a - b - 0\n")
        report = self._report([(2, 1), (5, 1)])
        result = self._run(report, self._floor())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Patch coverage OK.", result.stdout)

    @unittest.skipUnless(_HAS_DIFF_COVER, "diff-cover not installed")
    def test_ShouldFail_WhenPatchCoverageBelowFloor(self):
        # feature changes line 2 (covered) and line 5 (UNcovered) -> 50% < 85%
        self._commit_feature(
            "def add(a, b):\n    return a + b + 0\n\n"
            "def sub(a, b):\n    return a - b - 0\n")
        report = self._report([(2, 1), (5, 0)])
        result = self._run(report, self._floor())
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("below the floor", result.stdout)

    @unittest.skipUnless(_HAS_DIFF_COVER, "diff-cover not installed")
    def test_ShouldFail_WhenDiffTouchesSourceButReportMeasuresZero(self):
        # A committed .cs change (cobertura source), but the report references a
        # DIFFERENT filename -> diff-cover measures 0 diff lines though .cs source
        # changed -> the mis-wired-report liveness self-test must trip loud.
        _git(self.repo, "checkout", "-q", "-b", "feature")
        self._write("src/Thing.cs", "class Thing { int F() { return 1; } }\n")
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "add cs")
        # Report points at an unrelated file, so Thing.cs's changed lines map to
        # nothing measurable.
        text = _cobertura([(2, 1)], filename="src/unrelated.py").replace(
            "{ROOT}", self.repo)
        report = os.path.join(self.repo, "Cobertura.xml")
        with open(report, "w", encoding="utf-8") as handle:
            handle.write(text)
        result = self._run(report, self._floor())
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("mis-wired", result.stdout)

    def test_ShouldFail_WhenMergeBaseUnresolved(self):
        # A fresh repo with an unrelated orphan HEAD and NO origin/main ref: the
        # merge-base cannot resolve -> shallow-clone signature -> FAIL loud.
        shallow = tempfile.mkdtemp()
        try:
            _git(shallow, "init", "-q")
            _git(shallow, "config", "user.email", "t@t.co")
            _git(shallow, "config", "user.name", "t")
            _git(shallow, "checkout", "-q", "-b", "feature")
            with open(os.path.join(shallow, "a.txt"), "w", encoding="utf-8") as h:
                h.write("x")
            _git(shallow, "add", "-A")
            _git(shallow, "commit", "-qm", "only")
            report = os.path.join(shallow, "Cobertura.xml")
            with open(report, "w", encoding="utf-8") as h:
                h.write(_cobertura([(2, 1)]).replace("{ROOT}", shallow))
            floor = os.path.join(shallow, "floor.json")
            with open(floor, "w", encoding="utf-8") as h:
                h.write(_FLOOR)
            result = subprocess.run(
                [sys.executable, _SCRIPT, report, floor],
                cwd=shallow, capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
            self.assertIn("shallow clone", result.stdout)
        finally:
            shutil.rmtree(shallow, ignore_errors=True)


if __name__ == "__main__":
    unittest.main()
