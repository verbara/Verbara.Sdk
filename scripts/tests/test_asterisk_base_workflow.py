"""Guards: the Asterisk base image workflow never runs on a schedule and never publishes from a pull request.

`.github/workflows/asterisk-base.yml` is the one workflow here that can push an image, sign it and attest to it.
Two properties of it are decisions, and this file is what enforces them:

- **Never on a schedule.** The image is pinned by digest, and a pin by digest never moves; a rebuild happens only
  when someone wants a re-pin. A scheduled rebuild would publish images nobody asked for (and nobody measured).
  So the workflow's triggers are exactly `workflow_dispatch` and `pull_request`: no `schedule`, no `push`, no
  `workflow_run`, no `pull_request_target`.
- **Never a push from a pull request.** A pull request builds and gates the recipe; publishing is a dispatch. So
  every job a `pull_request` run can reach holds no push primitive (`docker push`, `docker login`, `--push`,
  `push: true`, `cosign sign`/`attest`, `crane`/`skopeo`/`oras` copies, a login or build-push action) and no write
  permission (`packages: write`, `id-token: write`), and the workflow-level permissions grant neither. A job counts
  as unreachable from a pull request only when its own `if:` requires `github.event_name == 'workflow_dispatch'`
  and holds no `||`.

Every action of the workflow is also pinned by a full commit sha: the publishing job holds a write token and an
OIDC identity, so a moved tag must not change what runs with them.

The reader is a line-and-indentation reader of the workflow's own shape (stdlib only, no pip deps, matching the
other guard-script tests). What it cannot see: a push made by a tool it does not know, or a command assembled from
variables or read from a file.
"""
import os
import re
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.abspath(os.path.join(_HERE, os.pardir, os.pardir))
WORKFLOW = os.path.join(_REPO, ".github", "workflows", "asterisk-base.yml")

ALLOWED_TRIGGERS = {"workflow_dispatch", "pull_request"}
PULL_REQUEST_TRIGGERS = {"pull_request", "pull_request_target"}

_COMMENT_LINE = re.compile(r"^\s*#")
_KEY = re.compile(r"^(?P<indent>\s*)(?P<key>[\"']?[A-Za-z0-9_.\-]+[\"']?)\s*:(?P<rest>.*)$")
_PUSH_PRIMITIVES = [
    ("docker push", re.compile(r"\bdocker\s+(?:image\s+)?push\b")),
    ("docker login", re.compile(r"\bdocker\s+login\b")),
    ("--push", re.compile(r"(?<![\w-])--push\b")),
    ("push: true", re.compile(r"^\s*push:\s*[\"']?true\b")),
    ("cosign sign", re.compile(r"\bcosign\s+sign\b")),
    ("cosign attest", re.compile(r"\bcosign\s+attest\b")),
    ("crane push/copy", re.compile(r"\bcrane\s+(?:push|copy|cp|append|mutate|tag)\b")),
    ("skopeo copy", re.compile(r"\bskopeo\s+(?:copy|sync)\b")),
    ("oras push", re.compile(r"\boras\s+(?:push|attach|copy|cp)\b")),
    ("login action", re.compile(r"uses:\s*[\"']?docker/login-action@")),
    ("build-push action", re.compile(r"uses:\s*[\"']?docker/build-push-action@")),
    ("packages: write", re.compile(r"^\s*packages:\s*write\b")),
    ("id-token: write", re.compile(r"^\s*id-token:\s*write\b")),
    ("write-all", re.compile(r"^\s*permissions:\s*write-all\b")),
]
_DISPATCH_ONLY = re.compile(r"github\.event_name\s*==\s*'workflow_dispatch'")
_USES = re.compile(r"^\s*(?:-\s+)?uses:\s*[\"']?(?P<ref>[^\s\"'#]+)")
_FULL_SHA = re.compile(r"@[0-9a-f]{40}$")


def _code_lines(text):
    """(1-based number, line) for every line that is not blank and not a full-line comment."""
    return [(i + 1, line) for i, line in enumerate(text.split("\n"))
            if line.strip() and not _COMMENT_LINE.match(line)]


def _indent(line):
    return len(line) - len(line.lstrip(" "))


def _unquote(key):
    return key.strip("\"'")


def _block(lines, start):
    """The lines strictly more indented than lines[start], up to the first line that is not."""
    base = _indent(lines[start][1])
    out = []
    for number, line in lines[start + 1:]:
        if _indent(line) <= base:
            break
        out.append((number, line))
    return out


def _top_level(lines, name):
    """(index, rest-of-line) of the top-level key `name`, or (None, None)."""
    for index, (_, line) in enumerate(lines):
        match = _KEY.match(line)
        if match and not match.group("indent") and _unquote(match.group("key")) in name:
            return index, match.group("rest").strip()
    return None, None


def _children(block):
    """The direct child keys of a block: {key: (rest-of-line, its own block)}."""
    if not block:
        return {}
    child_indent = min(_indent(line) for _, line in block)
    out = {}
    for index, (_, line) in enumerate(block):
        match = _KEY.match(line)
        if match and _indent(line) == child_indent and not line.lstrip().startswith("-"):
            out[_unquote(match.group("key"))] = (match.group("rest").strip(), _block(block, index))
    return out


def triggers(text):
    """The workflow's trigger names (`on:` as a scalar, a flow list or a mapping)."""
    lines = _code_lines(text)
    index, rest = _top_level(lines, {"on", "true"})
    if index is None:
        return set()
    rest = rest.split("#", 1)[0].strip()
    if rest.startswith("["):
        return {_unquote(t.strip()) for t in rest.strip("[]").split(",") if t.strip()}
    if rest:
        return {_unquote(rest)}
    block = _block(lines, index)
    names = set(_children(block))
    names.update(_unquote(line.strip()[1:].strip()) for _, line in block
                 if line.strip().startswith("-") and _indent(line) == min(_indent(l) for _, l in block))
    return names


def workflow_permission_findings(text):
    """A finding for every write grant in the workflow-level `permissions:`."""
    lines = _code_lines(text)
    index, rest = _top_level(lines, {"permissions"})
    if index is None:
        return ["the workflow sets no top-level permissions, so every job inherits the repository default token"]
    findings = []
    if "write" in rest:
        findings.append(f"workflow-level permissions: {rest}")
    for number, line in _block(lines, index):
        if re.search(r":\s*write\b", line):
            findings.append(f"line {number}: workflow-level {line.strip()} reaches every job, pull requests included")
    return findings


def jobs(text):
    """{job id: (job-level `if:` text or '', the job's lines)}."""
    lines = _code_lines(text)
    index, _ = _top_level(lines, {"jobs"})
    if index is None:
        return {}
    out = {}
    for job_id, (_, job_block) in _children(_block(lines, index)).items():
        condition = _children(job_block).get("if", ("", []))[0]
        out[job_id] = (condition, job_block)
    return out


def is_dispatch_only(condition):
    return bool(_DISPATCH_ONLY.search(condition)) and "||" not in condition


def pull_request_push_findings(text):
    """A finding for every push primitive or write permission in a job a pull_request run can reach."""
    if not triggers(text) & PULL_REQUEST_TRIGGERS:
        return []
    findings = []
    for job_id, (condition, block) in jobs(text).items():
        if is_dispatch_only(condition):
            continue
        for number, line in block:
            for label, pattern in _PUSH_PRIMITIVES:
                if pattern.search(line):
                    findings.append(f"job {job_id}, line {number}: {label} in a job a pull request reaches: {line.strip()}")
    return findings


def push_primitives(block):
    return {label for _, line in block for label, pattern in _PUSH_PRIMITIVES if pattern.search(line)}


def unpinned_actions(text):
    findings = []
    for number, line in _code_lines(text):
        match = _USES.match(line)
        if match and not match.group("ref").startswith("./") and not _FULL_SHA.search(match.group("ref")):
            findings.append(f"line {number}: {match.group('ref')} is not pinned by a full commit sha")
    return findings


def _read(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


class AsteriskBaseWorkflowTests(unittest.TestCase):
    """The real workflow."""

    @classmethod
    def setUpClass(cls):
        cls.text = _read(WORKFLOW)

    def test_ShouldTriggerOnlyOnDispatchAndPullRequest_WhenReadingTheWorkflow(self):
        found = triggers(self.text)
        self.assertEqual(found, ALLOWED_TRIGGERS,
                         "the base image is rebuilt only when someone wants a re-pin: no schedule, no push trigger")
        self.assertNotIn("schedule", found)

    def test_ShouldReachNoPushFromAPullRequest_WhenReadingTheWorkflow(self):
        self.assertEqual(pull_request_push_findings(self.text), [])
        self.assertEqual(workflow_permission_findings(self.text), [])

    def test_ShouldPublishOnlyFromADispatchOnlyJob_WhenReadingTheWorkflow(self):
        found = jobs(self.text)
        publishers = {job_id for job_id, (_, block) in found.items() if push_primitives(block)}
        self.assertEqual(publishers, {"publish"}, "exactly one job publishes")
        condition, block = found["publish"]
        self.assertTrue(is_dispatch_only(condition), condition)
        self.assertIn("github.ref == 'refs/heads/main'", condition, "a publish runs from main only")
        # A guard that matched nothing would pass on nothing: the publishing job must show every primitive it uses.
        self.assertTrue({"docker push", "docker login", "cosign sign", "cosign attest",
                         "packages: write", "id-token: write"} <= push_primitives(block), push_primitives(block))
        self.assertGreaterEqual(len(found), 2, "the pull-request build job must be read too")

    def test_ShouldPinEveryActionByCommitSha_WhenReadingTheWorkflow(self):
        self.assertEqual(unpinned_actions(self.text), [])
        self.assertTrue(any(_USES.match(line) for _, line in _code_lines(self.text)), "the reader found no action")


_GOOD = """name: x
on:
  workflow_dispatch:
  pull_request:
    paths: ["docker/base/**"]
permissions:
  contents: read
jobs:
  build:
    if: github.event_name == 'pull_request'
    runs-on: ubuntu-latest
    steps:
      - run: docker build -t local/x docker/base
  publish:
    if: github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main'
    permissions:
      packages: write
      id-token: write
    steps:
      - run: docker push "$REF"
      - run: cosign sign --yes "$SUBJECT"
"""


class AsteriskBaseWorkflowFixtureTests(unittest.TestCase):
    """Negative controls: the reader reports each way the decisions can break."""

    def test_ShouldPass_WhenTheFixtureFollowsBothDecisions(self):
        self.assertEqual(triggers(_GOOD), ALLOWED_TRIGGERS)
        self.assertEqual(pull_request_push_findings(_GOOD), [])
        self.assertEqual(workflow_permission_findings(_GOOD), [])

    def test_ShouldReportTheTrigger_WhenAScheduleIsAdded(self):
        text = _GOOD.replace("  workflow_dispatch:\n", "  workflow_dispatch:\n  schedule:\n    - cron: '0 3 * * 1'\n")
        self.assertIn("schedule", triggers(text))
        self.assertNotEqual(triggers(text), ALLOWED_TRIGGERS)

    def test_ShouldReportTheTrigger_WhenOnIsAFlowListWithSchedule(self):
        text = _GOOD.replace("on:\n  workflow_dispatch:\n  pull_request:\n    paths: [\"docker/base/**\"]\n",
                             "on: [workflow_dispatch, schedule]\n")
        self.assertEqual(triggers(text), {"workflow_dispatch", "schedule"})

    def test_ShouldReportThePush_WhenThePullRequestJobPushes(self):
        text = _GOOD.replace("      - run: docker build -t local/x docker/base\n",
                             "      - run: docker build -t local/x docker/base\n      - run: docker push local/x\n")
        findings = pull_request_push_findings(text)
        self.assertEqual(len(findings), 1, findings)
        self.assertIn("job build", findings[0])

    def test_ShouldReportThePublishJob_WhenItsConditionAlsoAdmitsAPullRequest(self):
        text = _GOOD.replace("if: github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main'",
                             "if: github.event_name == 'workflow_dispatch' || github.event_name == 'pull_request'")
        findings = pull_request_push_findings(text)
        self.assertTrue(any("job publish" in f and "docker push" in f for f in findings), findings)
        self.assertTrue(any("packages: write" in f for f in findings), findings)

    def test_ShouldReportThePublishJob_WhenItHasNoCondition(self):
        text = _GOOD.replace("    if: github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main'\n", "")
        self.assertTrue(any("job publish" in f for f in pull_request_push_findings(text)))

    def test_ShouldReportTheGrant_WhenTheWorkflowLevelPermissionsWritePackages(self):
        text = _GOOD.replace("permissions:\n  contents: read\njobs:", "permissions:\n  contents: read\n  packages: write\njobs:")
        self.assertEqual(len(workflow_permission_findings(text)), 1)

    def test_ShouldReportTheAction_WhenItIsPinnedByTag(self):
        text = _GOOD.replace("      - run: docker push \"$REF\"\n", "      - uses: docker/login-action@v3\n")
        self.assertEqual(len(unpinned_actions(text)), 1)
        self.assertEqual(unpinned_actions(text.replace("@v3", "@" + "a" * 40)), [])


if __name__ == "__main__":
    unittest.main()
