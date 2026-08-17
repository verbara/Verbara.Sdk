"""Unit tests for check-patch-coverage.py (the diff-coverage primary gate).

Most tests build a throwaway git repo (a local "origin/main" ref + a feature
HEAD with committed changes), a fixture Cobertura report, and run the script
end to end with CWD inside the repo. Cases: below-patch FAIL, above PASS,
shallow-clone (unresolved merge-base) FAIL, touched-but-zero (mis-wired report)
FAIL, and the several shapes that are legitimately unmeasurable and must pass.

Two cases assert the liveness EVIDENCE RULE directly on the predicate rather than
end to end, because a fixture cannot produce the case: a report whose paths line up
well enough to instrument a given line is a report diff-cover would also score, so
"instrumented yet unmeasured" only exists at that seam.

The end-to-end cases are skipped when diff-cover is not installed so
`python3 -m unittest` still runs offline; CI installs the pinned diff-cover
(ADR-0013) so they always execute there. The shallow-clone case and the two
predicate cases need no diff-cover and always run. Stdlib unittest only — NO pip deps.
"""
import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_SCRIPT = os.path.join(_HERE, os.pardir, "check-patch-coverage.py")

_HAS_DIFF_COVER = shutil.which("diff-cover") is not None


def _load_script():
    """Import check-patch-coverage.py as a module. Its filename is not a valid
    identifier, so this goes through importlib; the `__main__` guard keeps main()
    from running on import."""
    spec = importlib.util.spec_from_file_location("check_patch_coverage", _SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

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

    def _commit_annotated_thing(self):
        """Shared fixture for the line-level evidence rule. Base: a 4-line class.
        Feature: three ADDED lines — 3 (a comment), 4 (a C# attribute line) and 5 (its
        continuation) — none of which is a sequence point in any build. The method
        body is untouched and lands at line 6."""
        self._write("src/Acme.Core/Thing.cs",
                    "class Thing\n"
                    "{\n"
                    "    int F() { return 1; }\n"
                    "}\n")
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "thing")
        _git(self.repo, "update-ref", "refs/remotes/origin/main", "HEAD")
        _git(self.repo, "checkout", "-q", "-b", "feature")
        self._write("src/Acme.Core/Thing.cs",
                    "class Thing\n"
                    "{\n"
                    "    // See Pro/ADR-0017.\n"
                    "    [Log(Level = 1,\n"
                    '        Message = "hello {Name}")]\n'
                    "    int F() { return 1; }\n"
                    "}\n")
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "annotate")

    def _diff_adds(self, line_map):
        """Run the liveness predicate directly against this repo's diff, with a
        hand-built instrumented-line map standing in for a coverage report."""
        module = _load_script()
        merge_base = _git(self.repo, "merge-base", "origin/main", "HEAD").stdout.strip()
        previous = os.getcwd()
        os.chdir(self.repo)
        try:
            return module.diff_adds_executable_source(
                merge_base, (".cs",), module.instrumented_roots(line_map), line_map)
        finally:
            os.chdir(previous)

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

    @unittest.skipUnless(_HAS_DIFF_COVER, "diff-cover not installed")
    def test_ShouldPass_WhenExecutableChangeIsOutsideInstrumentedRoot(self):
        # A .cs change under a non-instrumented root (tooling/, not the report's
        # src/ root) adds an executable line but can never be measured -> n/a, NOT
        # a mis-wiring trip. This is the config/tooling counterpart to the mis-wired
        # case above (an import-path refactor or an eslint.config.js edit lands here).
        _git(self.repo, "checkout", "-q", "-b", "feature")
        self._write("tooling/Gen.cs", "class Gen { int F() { return 2; } }\n")
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "tooling")
        report = self._report([(2, 1)])  # covers src/calc.py -> instrumented root 'src'
        result = self._run(report, self._floor())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("n/a, pass", result.stdout)

    @unittest.skipUnless(_HAS_DIFF_COVER, "diff-cover not installed")
    def test_ShouldPass_WhenExecutableChangeIsInProjectAbsentFromReport(self):
        # A .cs change in a SECOND project the report does not instrument (a by-design
        # coverage-excluded *.Storage.Postgres project, verified only by the
        # integration-tests job) adds executable lines but can never be measured by THIS
        # report -> n/a, NOT a mis-wiring trip. The multi-project counterpart to the
        # outside-root case: the excluded project shares the 'src' top-level but is a
        # different PROJECT root, so a project-scoped liveness trip must let it pass.
        _git(self.repo, "checkout", "-q", "-b", "feature")
        self._write("src/Acme.Storage.Postgres/Store.cs",
                    "class Store { int F() { return 3; } }\n")
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "pg")
        # report instruments src/Acme.Core/... (project root 'src/Acme.Core'); the change
        # landed in src/Acme.Storage.Postgres/... (a different, uninstrumented project).
        text = _cobertura([(2, 1)], filename="src/Acme.Core/calc.py").replace(
            "{ROOT}", self.repo)
        report = os.path.join(self.repo, "Cobertura.xml")
        with open(report, "w", encoding="utf-8") as handle:
            handle.write(text)
        result = self._run(report, self._floor())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("n/a, pass", result.stdout)

    @unittest.skipUnless(_HAS_DIFF_COVER, "diff-cover not installed")
    def test_ShouldPass_WhenModifiedFileIsAbsentFromReport(self):
        # Modifying an EXISTING file the report does not carry — a pure interface /
        # declaration file (its added `Foo();` signature lines look executable but
        # produce no coverage), or an integration-only class no unit test loads —
        # measures nothing -> n/a, NOT a mis-wiring trip. A NEW file would still trip;
        # only an already-existing, report-absent file is waved through.
        self._write("src/Acme.Core/IThing.cs", "interface IThing { }\n")
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "iface")
        _git(self.repo, "update-ref", "refs/remotes/origin/main", "HEAD")
        _git(self.repo, "checkout", "-q", "-b", "feature")
        self._write(
            "src/Acme.Core/IThing.cs",
            "interface IThing {\n"
            "    System.Threading.Tasks.Task<int> GetAsync(string id);\n"
            "}\n")
        _git(self.repo, "add", "-A")
        _git(self.repo, "commit", "-qm", "add signature")
        # Report instruments a DIFFERENT file in the SAME project (so the project is
        # instrumented) but NOT IThing.cs (interfaces produce no coverage class).
        text = _cobertura([(2, 1)], filename="src/Acme.Core/Thing.cs").replace(
            "{ROOT}", self.repo)
        report = os.path.join(self.repo, "Cobertura.xml")
        with open(report, "w", encoding="utf-8") as handle:
            handle.write(text)
        result = self._run(report, self._floor())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("n/a, pass", result.stdout)

    @unittest.skipUnless(_HAS_DIFF_COVER, "diff-cover not installed")
    def test_ShouldPass_WhenAddedLinesAreNotInstrumented(self):
        # The PR #94 shape. An EXISTING file the report CARRIES gains only lines the
        # report does not instrument: a comment, a C# attribute line, and the
        # continuation line of a multi-line statement. None is a sequence point, so
        # diff-cover correctly measures 0 -> n/a, pass. Under the old text heuristic
        # the attribute and continuation lines read as executable and tripped the
        # mis-wiring self-test, turning a correct green into a red.
        self._commit_annotated_thing()
        # The report carries Thing.cs and instruments ONLY line 6 (the method body,
        # which the diff does not touch); the added lines are 3, 4 and 5.
        text = _cobertura([(6, 1)], filename="src/Acme.Core/Thing.cs").replace(
            "{ROOT}", self.repo)
        report = os.path.join(self.repo, "Cobertura.xml")
        with open(report, "w", encoding="utf-8") as handle:
            handle.write(text)
        result = self._run(report, self._floor())
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("n/a, pass", result.stdout)

    def test_DiffAddsExecutableSource_ShouldReturnTrue_WhenAddedLineIsInstrumented(self):
        # The evidence rule, asserted directly on the predicate. An added line whose
        # NUMBER the report instruments IS mis-wiring evidence: if the report says the
        # line is measurable and diff-cover scored nothing, the two do not line up.
        # Constructed in-process because an end-to-end fixture cannot produce it — a
        # report whose paths line up well enough to instrument line 3 is a report
        # diff-cover would also score, so the real mis-wiring can only be simulated
        # at this seam.
        self._commit_annotated_thing()
        self.assertTrue(self._diff_adds({"src/Acme.Core/Thing.cs": {3, 6}}))

    def test_DiffAddsExecutableSource_ShouldReturnFalse_WhenAddedLineIsNotInstrumented(self):
        # The other direction, on the same diff: the added lines are 3, 4 and 5; the
        # report instruments only 6. Nothing measurable was added, so a 0 measurement
        # is arithmetic and the trip must stand down. Paired with the test above, this
        # pins the rule rather than the absence of the trip.
        self._commit_annotated_thing()
        self.assertFalse(self._diff_adds({"src/Acme.Core/Thing.cs": {6}}))

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
