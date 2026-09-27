"""Guards: no workflow dispatches a consumer repository, and none runs the live provider probe.

Two recorded decisions that nothing enforced until now:

- **ADR-0040 D1 -- publishing does not cascade.** A release from this repository advances only this
  repository; it never implies, schedules or authorizes a consumer's pin bump. The mechanical way to
  break that is one step in a workflow that dispatches a workflow in Pro or Platform. So every
  dispatch primitive a workflow can reach is reported unless its target is provably this repository:
  `gh workflow run` (its `--repo`/`-R` flag, or a `GH_REPO` set in the file), the REST `dispatches`
  endpoints (`repos/<owner>/<repo>/dispatches` and `.../actions/workflows/<id>/dispatches`), an
  Actions-marketplace dispatch action, and Octokit's `createDispatchEvent`/`createWorkflowDispatch`.
  The last two carry their target in inputs this guard does not resolve, so they are reported
  whatever they target.
- **ADR-0048 D6 -- the probe is an operator-run instrument, off the PR path.** It sends real traffic
  to a paid vendor with a real credential. A `run:` step that names `probe-provider-conformance` and
  passes `--probe` -- or any prefix argparse expands to it (`--pro all` runs the probe too) -- is
  reported. Its `--self-check` and `--list` are offline and stay allowed.

Scope: every tracked YAML file under `.github/`, and every tracked script a line of one names by
path, followed one hop -- moving the command into `scripts/ci/x.sh` does not hide it. Full-line
comments (YAML and shell) are not commands and are skipped; backslash continuations are joined. The
probe script itself is not followed: its own docstring documents the `--probe` invocation.

What it cannot see: a command assembled from variables or read from a file, a script reached through
another script (one hop only), and a dispatch made by a tool it does not know.

Stdlib only, no pip deps, matching the other guard-script tests.
"""
import os
import re
import subprocess
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.abspath(os.path.join(_HERE, os.pardir, os.pardir))

SELF_REPOSITORY = "verbara/Verbara.Sdk"
PROBE_SCRIPT = "scripts/probe-provider-conformance.py"

# Measured on 2026-09-27: 9 tracked YAML files under .github/ (8 workflows + dependabot.yml),
# 55 `run:` steps, and 20 tracked scripts named by path from a workflow line.
MIN_WORKFLOW_FILES = 6
MIN_RUN_STEPS = 40
MIN_FOLLOWED_SCRIPTS = 10

_SCRIPT_SUFFIXES = (".sh", ".bash", ".py", ".js", ".mjs", ".ps1")
_COMMENT_LINE = re.compile(r"^\s*#")
_RUN_KEY = re.compile(r"^(?P<lead>\s*(?:-\s+)?)run:(?:\s+(?P<value>.*))?$")
_BLOCK_SCALAR = re.compile(r"^[|>][-+0-9]*\s*(?:#.*)?$")

_GH_COMMAND = re.compile(r"(?<![\w./-])gh\s+(?P<args>[^|;&\n]*)")
_WORKFLOW_RUN = re.compile(r"(?<![\w-])workflow\s+run(?![\w-])")
# A value is a quoted string, a `${{ expression }}` (which holds spaces), or a bare word.
_VALUE = r"(?P<repo>\"[^\"]*\"|'[^']*'|\$\{\{[^}]*\}\}|\S+)"
_GH_REPO_FLAG = re.compile(r"(?:(?<![\w-])--repo(?:=|\s+)|(?<![\w-])-R(?:=|\s*))" + _VALUE)
_GH_REPO_ENV = re.compile(r"(?<![\w])GH_REPO\s*[:=]\s*" + _VALUE)
_REST_DISPATCH = re.compile(
    r"repos/(?P<repo>.+?)/(?:actions/workflows/[^/\s\"']+/)?dispatches(?![\w-])")
_ANY_DISPATCH_ENDPOINT = re.compile(r"/dispatches(?![\w-])")
_OCTOKIT_DISPATCH = re.compile(r"\b(?:createDispatchEvent|createWorkflowDispatch)\b")
_USES = re.compile(r"^\s*(?:-\s+)?uses:\s*[\"']?(?P<ref>[^\s\"'#]+)")
_DISPATCH_ACTION = re.compile(r"(?i)^[^/@]+/[^@]*(?:dispatch|trigger-workflow)[^@]*(?:@|$)")

_PROBE_NAME = re.compile(r"probe[-_]provider[-_]conformance")
# argparse's allow_abbrev: with --probe the only option starting `--p`, every prefix selects it.
_PROBE_FLAG = re.compile(r"(?<![\w-])--p(?:r(?:o(?:b(?:e)?)?)?)?(?![\w-])")


def _unquote(value):
    return value.strip().strip("\"'").strip()


def is_self_repository(target):
    target = re.sub(r"\s+", "", _unquote(target))
    return target.lower() == SELF_REPOSITORY.lower() or target in {
        "${{github.repository}}", "$GITHUB_REPOSITORY", "${GITHUB_REPOSITORY}",
        "{owner}/{repo}", ":owner/:repo",
    }


def logical_lines(text):
    """Yield (line_number, text) per command: comment lines dropped, `\\` continuations joined."""
    pending, start = [], None
    for number, line in enumerate(text.splitlines(), start=1):
        if _COMMENT_LINE.match(line):
            continue
        if start is None:
            start = number
        stripped = line.rstrip()
        if stripped.endswith("\\"):
            pending.append(stripped[:-1])
            continue
        pending.append(stripped)
        yield start, " ".join(part.strip() for part in pending)
        pending, start = [], None
    if pending:
        yield start, " ".join(part.strip() for part in pending)


def run_steps(text):
    """Yield (first_line, body) for each `run:` value in a workflow: the body as its own text, and
    the file line its first body line sits on (the `run:` line for an inline value, the next one for
    a block scalar)."""
    lines = text.splitlines()
    i = 0
    while i < len(lines):
        match = _RUN_KEY.match(lines[i])
        if not match:
            i += 1
            continue
        key_column = len(match.group("lead"))
        value = (match.group("value") or "").rstrip()
        inline = bool(value) and not _BLOCK_SCALAR.match(value)
        body = [_unquote(value)] if inline else []
        first_line = i + 1 if inline else i + 2
        j = i + 1
        while j < len(lines):
            line = lines[j]
            if line.strip() and len(line) - len(line.lstrip()) <= key_column:
                break
            body.append(line.strip())
            j += 1
        yield first_line, "\n".join(body)
        i = j


def foreign_gh_repos(text):
    """Every `GH_REPO` value set in `text` that is not this repository."""
    return [m.group("repo") for _, line in logical_lines(text) for m in _GH_REPO_ENV.finditer(line)
            if not is_self_repository(m.group("repo"))]


def dispatch_findings(text, inherited_gh_repos=()):
    """Return [(line_number, finding)] for every dispatch in `text` not provably aimed at this repo.

    `inherited_gh_repos` carries the workflow's foreign `GH_REPO` into a script it runs, where a
    bare `gh workflow run` inherits it from the environment."""
    findings = []
    lines = list(logical_lines(text))
    foreign_env = [*inherited_gh_repos, *foreign_gh_repos(text)]
    for number, line in lines:
        for gh in _GH_COMMAND.finditer(line):
            args = gh.group("args")
            if not _WORKFLOW_RUN.search(args):
                continue
            flag = _GH_REPO_FLAG.search(args)
            if flag:
                if not is_self_repository(flag.group("repo")):
                    findings.append(
                        (number, f"`gh workflow run` targets {_unquote(flag.group('repo'))}"))
            elif foreign_env:
                findings.append(
                    (number, f"`gh workflow run` targets GH_REPO={_unquote(foreign_env[0])}"))
        resolved = set()
        for rest in _REST_DISPATCH.finditer(line):
            resolved.add(rest.end())
            if not is_self_repository(rest.group("repo")):
                findings.append((number, f"a REST dispatch targets {_unquote(rest.group('repo'))}"))
        for endpoint in _ANY_DISPATCH_ENDPOINT.finditer(line):
            if endpoint.end() not in resolved:
                findings.append((number, "a REST dispatch whose target repository is not named"))
        if _OCTOKIT_DISPATCH.search(line):
            findings.append((number, "an Octokit dispatch call; its target is an input this guard "
                                     "does not resolve"))
        uses = _USES.match(line)
        if uses and _DISPATCH_ACTION.match(uses.group("ref")):
            findings.append((number, f"a dispatch action ({uses.group('ref')}); its target is an "
                                     "input this guard does not resolve"))
    return findings


def probe_findings(body):
    """Return [(line within body, finding)] when one step's body runs the live probe."""
    commands = list(logical_lines(body))
    if not any(_PROBE_NAME.search(line) for _, line in commands):
        return []
    return [(number, f"runs the live provider probe ({flag.group(0)})")
            for number, line in commands for flag in _PROBE_FLAG.finditer(line)]


def followed_scripts(text, tracked_files):
    """Tracked scripts a non-comment line of `text` names by path (the probe script excluded)."""
    named = set()
    for _, line in logical_lines(text):
        for path in tracked_files:
            if path == PROBE_SCRIPT or not path.endswith(_SCRIPT_SUFFIXES) or path not in line:
                continue
            # `./scripts/x.sh` and `"$GITHUB_WORKSPACE/scripts/x.sh"` name it too.
            if re.search(r"(?<![\w.-])" + re.escape(path) + r"(?![\w./-])", line):
                named.add(path)
    return sorted(named)


def scan(workflows, scripts, tracked_files):
    """workflows, scripts: {repo-relative path: text}. Returns (violations, steps, followed)."""
    violations, steps, followed = [], 0, set()
    for source in sorted(workflows):
        text = workflows[source]
        for number, finding in dispatch_findings(text):
            violations.append(f"{source}:{number}: {finding} (ADR-0040 D1: publishing does not "
                              "cascade)")
        for first_line, body in run_steps(text):
            steps += 1
            for number, finding in probe_findings(body):
                violations.append(f"{source}:{first_line + number - 1}: {finding} (ADR-0048 D6: "
                                  "the probe is operator-run, off the PR path)")
        for script in followed_scripts(text, tracked_files):
            followed.add(script)
            body = scripts.get(script, "")
            for number, finding in dispatch_findings(body, foreign_gh_repos(text)):
                violations.append(f"{source} -> {script}:{number}: {finding} (ADR-0040 D1)")
            for number, finding in probe_findings(body):
                violations.append(f"{source} -> {script}:{number}: {finding} (ADR-0048 D6)")
    return violations, steps, followed


def git_ls_files(repo):
    out = subprocess.run(["git", "-C", repo, "ls-files", "-z"],
                         capture_output=True, check=True).stdout
    return [p for p in out.decode("utf-8").split("\0") if p]


def _read(path):
    with open(os.path.join(_REPO, path), encoding="utf-8", errors="replace") as handle:
        return handle.read()


class WorkflowDecisionGuardTests(unittest.TestCase):
    """The two guards over the real tree."""

    @classmethod
    def setUpClass(cls):
        cls.tracked = git_ls_files(_REPO)
        cls.workflows = {p: _read(p) for p in cls.tracked
                         if p.startswith(".github/") and p.endswith((".yml", ".yaml"))}
        cls.scripts = {p: _read(p) for p in cls.tracked
                       if p.endswith(_SCRIPT_SUFFIXES) and os.path.isfile(os.path.join(_REPO, p))}
        cls.violations, cls.steps, cls.followed = scan(cls.workflows, cls.scripts, cls.tracked)

    def test_ShouldScanANonTrivialPopulation_WhenReadingTheRepository(self):
        self.assertGreaterEqual(
            len(self.workflows), MIN_WORKFLOW_FILES,
            f"liveness floor: {len(self.workflows)} tracked YAML files under .github/ -- is this a "
            "git checkout?")
        self.assertGreaterEqual(
            self.steps, MIN_RUN_STEPS,
            f"liveness floor: {self.steps} `run:` steps found -- has the step extractor stopped "
            "matching?")
        self.assertGreaterEqual(
            len(self.followed), MIN_FOLLOWED_SCRIPTS,
            f"liveness floor: followed {len(self.followed)} tracked scripts -- has path matching "
            "stopped working?")

    def test_ShouldFindNoDispatchOfAnotherRepository_WhenScanningEveryWorkflow(self):
        found = [v for v in self.violations if "ADR-0040" in v]
        self.assertEqual(
            [], found,
            "a workflow dispatches a repository other than this one. Publishing does not "
            "cascade: a consumer's pin bump is its own change, opened explicitly (ADR-0040 D1):\n  "
            + "\n  ".join(found))

    def test_ShouldFindNoRunOfTheLiveProbe_WhenScanningEveryWorkflow(self):
        found = [v for v in self.violations if "ADR-0048" in v]
        self.assertEqual(
            [], found,
            "a workflow runs the live provider probe. It spends a real credential on a paid "
            "vendor and makes the merge path depend on that vendor being reachable; it is run by "
            "an operator, by hand (ADR-0048 D6). `--self-check` and `--list` are offline and "
            "allowed:\n  "
            + "\n  ".join(found))


def _workflow(*step_lines):
    return "\n".join([
        "name: Release",
        "on:",
        "  push:",
        "    tags: ['v*']",
        "jobs:",
        "  publish:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        *step_lines,
    ]) + "\n"


class DispatchGuardFixtureTests(unittest.TestCase):
    """ADR-0040 D1 fixtures: what must be refused, and what must not be."""

    def _scan(self, workflow, scripts=None):
        scripts = scripts or {}
        violations, _, _ = scan({".github/workflows/publish.yml": workflow}, scripts,
                                [".github/workflows/publish.yml", *scripts])
        return violations

    def test_ShouldReportTheStep_WhenAWorkflowDispatchesAConsumerWithGhWorkflowRun(self):
        violations = self._scan(_workflow(
            "      - name: Bump the SDK pin in Pro",
            "        run: gh workflow run bump-sdk.yml --repo verbara/Verbara.Sdk.Pro -f v=2.6.1",
        ))

        self.assertEqual(1, len(violations), violations)
        self.assertIn(".github/workflows/publish.yml:10:", violations[0])
        self.assertIn("verbara/Verbara.Sdk.Pro", violations[0])

    def test_ShouldReport_WhenTheRepositoryFlagIsShortOrContinuedOnTheNextLine(self):
        violations = self._scan(_workflow(
            "      - run: |",
            "          gh workflow run bump-sdk.yml \\",
            "            -R verbara/Verbara.Platform",
            "          gh -R=verbara/Verbara.Platform.Web workflow run pin.yml",
        ))

        self.assertEqual(2, len(violations), violations)

    def test_ShouldReport_WhenGhRepoPointsTheImplicitTargetAtAConsumer(self):
        violations = self._scan(_workflow(
            "      - env:",
            "          GH_REPO: verbara/Verbara.Platform",
            "        run: gh workflow run bump-sdk.yml",
        ))

        self.assertEqual(1, len(violations), violations)
        self.assertIn("GH_REPO=verbara/Verbara.Platform", violations[0])

    def test_ShouldReport_WhenAScriptTheWorkflowRunsInheritsAForeignGhRepo(self):
        violations = self._scan(
            _workflow(
                "      - env:",
                "          GH_REPO: verbara/Verbara.Sdk.Pro",
                "        run: bash scripts/ci/bump.sh",
            ),
            {"scripts/ci/bump.sh": "#!/usr/bin/env bash\ngh workflow run bump-sdk.yml\n"})

        self.assertEqual(1, len(violations), violations)
        self.assertIn("-> scripts/ci/bump.sh:2:", violations[0])

    def test_ShouldReport_WhenARestDispatchTargetsAnotherRepository(self):
        violations = self._scan(_workflow(
            "      - run: |",
            "          gh api -X POST repos/verbara/Verbara.Sdk.Pro/dispatches -f event_type=sdk",
            "          curl -X POST https://api.github.com/repos/verbara/Verbara.Platform"
            "/actions/workflows/bump.yml/dispatches",
            '          curl -X POST "$API/dispatches"',
        ))

        self.assertEqual(3, len(violations), violations)
        self.assertIn("not named", violations[2])

    def test_ShouldReport_WhenAStepUsesADispatchActionOrAnOctokitDispatch(self):
        violations = self._scan(_workflow(
            "      - uses: peter-evans/repository-dispatch@v3",
            "        with:",
            "          repository: verbara/Verbara.Sdk.Pro",
            "      - uses: actions/github-script@v7",
            "        with:",
            "          script: await github.rest.actions.createWorkflowDispatch({owner, repo})",
        ))

        self.assertEqual(2, len(violations), violations)

    def test_ShouldReport_WhenTheDispatchLivesInAScriptTheWorkflowRuns(self):
        # Moving the command into scripts/ci/ must not hide it.
        violations = self._scan(
            _workflow("      - run: bash scripts/ci/notify-consumers.sh \"$VERSION\""),
            {"scripts/ci/notify-consumers.sh":
                "#!/usr/bin/env bash\n# Tell Pro about the release.\n"
                "gh workflow run bump-sdk.yml --repo verbara/Verbara.Sdk.Pro\n"})

        self.assertEqual(1, len(violations), violations)
        self.assertIn("-> scripts/ci/notify-consumers.sh:3:", violations[0])

    def test_ShouldAccept_WhenTheDispatchTargetsThisRepository(self):
        violations = self._scan(_workflow(
            "      - run: |",
            "          gh workflow run ci.yml",
            "          gh workflow run ci.yml --repo verbara/Verbara.Sdk",
            '          gh workflow run ci.yml -R "${{ github.repository }}"',
            "          gh api -X POST repos/{owner}/{repo}/dispatches -f event_type=x",
            '          curl -X POST "https://api.github.com/repos/$GITHUB_REPOSITORY/dispatches"',
            "          gh api repos/${{ github.repository }}/actions/workflows/ci.yml/dispatches",
        ))

        self.assertEqual([], violations)

    def test_ShouldAccept_WhenTheDispatchIsOnlyAComment(self):
        # perf-regression.yml documents its manual run in a YAML comment; a comment is not a step.
        violations = self._scan(_workflow(
            "      # gh workflow run bump.yml --repo verbara/Verbara.Sdk.Pro",
            "      - run: |",
            "          # never: gh workflow run bump.yml --repo verbara/Verbara.Platform",
            "          echo released",
        ))

        self.assertEqual([], violations)

    def test_ShouldAccept_WhenGhRunsSomethingOtherThanAWorkflow(self):
        violations = self._scan(_workflow(
            "      - run: gh run list --repo verbara/Verbara.Sdk.Pro --workflow ci.yml",
            "      - run: gh release create \"$TAG\" --repo verbara/Verbara.Sdk",
        ))

        self.assertEqual([], violations)


class ProbeGuardFixtureTests(unittest.TestCase):
    """ADR-0048 D6 fixtures: what must be refused, and what must not be."""

    def _scan(self, workflow, scripts=None):
        scripts = scripts or {}
        tracked = [".github/workflows/ci.yml", PROBE_SCRIPT, *scripts]
        violations, _, _ = scan({".github/workflows/ci.yml": workflow}, scripts, tracked)
        return violations

    def test_ShouldReportTheLine_WhenAStepRunsTheProbe(self):
        violations = self._scan(_workflow(
            "      - name: Conformance",
            "        run: |",
            "          set -euo pipefail",
            "          python3 scripts/probe-provider-conformance.py --probe all",
        ))

        self.assertEqual(1, len(violations), violations)
        self.assertIn(".github/workflows/ci.yml:12:", violations[0])
        self.assertIn("ADR-0048 D6", violations[0])

    def test_ShouldReport_WhenTheProbeRunsInlineOrWithAnAbbreviatedOrContinuedFlag(self):
        for run in (
            "        run: python3 scripts/probe-provider-conformance.py --probe=deepgram-stt",
            "        run: ./scripts/probe-provider-conformance.py --pro all",
            "        run: |\n          python3 scripts/probe-provider-conformance.py \\\n"
            "            --probe all",
            "        run: |\n          PROBE=scripts/probe-provider-conformance.py\n"
            "          python3 \"$PROBE\" --probe all",
        ):
            with self.subTest(run=run):
                self.assertEqual(1, len(self._scan(_workflow("      - name: p", run))))

    def test_ShouldReport_WhenAScriptTheWorkflowRunsRunsTheProbe(self):
        violations = self._scan(
            _workflow('      - run: bash "${{ github.workspace }}/scripts/ci/conformance.sh"'),
            {"scripts/ci/conformance.sh":
                "#!/usr/bin/env bash\npython3 scripts/probe-provider-conformance.py --probe all\n"})

        self.assertEqual(1, len(violations), violations)
        self.assertIn("-> scripts/ci/conformance.sh:2:", violations[0])

    def test_ShouldAccept_WhenAStepRunsTheProbesOfflineModes(self):
        violations = self._scan(_workflow(
            "      - run: python3 scripts/probe-provider-conformance.py --self-check",
            "      - run: python3 scripts/probe-provider-conformance.py --list",
            "      - run: python3 -m unittest discover scripts/tests",
        ))

        self.assertEqual([], violations)

    def test_ShouldAccept_WhenTheFlagAndTheProbeAreInDifferentSteps(self):
        violations = self._scan(_workflow(
            "      - run: python3 scripts/probe-provider-conformance.py --self-check",
            "      - run: ./tools/other.sh --probe",
        ))

        self.assertEqual([], violations)

    def test_ShouldAccept_WhenTheProbeIsOnlyNamedInAComment(self):
        violations = self._scan(_workflow(
            "      - run: |",
            "          # never here: python3 scripts/probe-provider-conformance.py --probe all",
            "          echo ok",
        ))

        self.assertEqual([], violations)


if __name__ == "__main__":
    unittest.main()
