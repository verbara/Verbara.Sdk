"""Guard: every relative link in a tracked Markdown file resolves to a tracked path.

#322 made `docs/decisions/`, `docs/plans/`, `docs/research/`, `docs/specs/` and `openspec/`
local-only (verbara-meta/ADR-0024), and eleven links into them were left behind in public files:
dead for every reader of the published repository, and green on every check, because nothing reads
a link. #324 removed them. This guard is what keeps the next one out.

It lives here, not in the .NET Governance tests, because a dead link is introduced by exactly the
kind of pull request that skips the Unit Tests job: a docs-only one. Coverage Script Tests runs on
every pull request (`.github/workflows/ci.yml`, `python3 -m unittest discover scripts/tests`).

What counts as a link: inline links and images `[text](target)`, reference definitions
`[label]: target` (a footnote `[^n]:` is not one), and HTML `href`/`src` attributes. Fenced code
blocks, inline code spans and HTML comments are not rendered as links and are skipped. A target with
a URL scheme, a bare `#fragment` or a protocol-relative `//host` is not relative and is not checked.

How a target resolves: against the linking file's directory, the way GitHub resolves it on the
file's `blob` page; a target starting with `/` resolves from the repository root. The fragment and
query are dropped and the path is percent-decoded. The result must be a tracked file, or a directory
holding one (GitHub renders a directory link as its tree), compared case-sensitively as GitHub does.
A target that climbs exactly two levels above the root leaves `/<owner>/<repo>/blob/<ref>/` for
`/<owner>/<repo>/`, which is GitHub's own UI -- `SECURITY.md`'s `../../security` is the case -- and
is accepted when its first segment is a GitHub repository page.

What it cannot see: absolute `https://github.com/...` links into this repository (they are not
relative), anchors inside a resolved file, indented (four-space) code blocks, and links a Markdown
renderer would build from anything other than the shapes above.

Stdlib only, no pip deps, matching the other guard-script tests.
"""
import os
import posixpath
import re
import subprocess
import unittest
import urllib.parse

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.abspath(os.path.join(_HERE, os.pardir, os.pardir))

# Pages GitHub serves under /<owner>/<repo>/ that are not repository content. A link reaches one by
# climbing exactly two levels above the root: out of `<ref>/`, then out of `blob/`.
GITHUB_REPO_PAGES = frozenset({
    "actions", "branches", "commits", "compare", "discussions", "forks", "graphs", "issues",
    "labels", "milestones", "network", "projects", "pulls", "pulse", "releases", "security",
    "stargazers", "tags", "wiki",
})

# The floor the real-tree scan must clear, so an extractor that silently stops finding links (a
# regex that no longer matches, a `git ls-files` that returns nothing) fails instead of passing.
# Measured on 2026-09-27: 84 tracked Markdown files, 126 relative links.
MIN_MARKDOWN_FILES = 60
MIN_RELATIVE_LINKS = 90

_FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
_CODE_SPAN = re.compile(r"(`+)(?:(?!\1).)+?\1")
_INLINE_TARGET = re.compile(r"\]\(\s*(<[^>\n]*>|[^)\s]+)")
_REFERENCE_DEFINITION = re.compile(r"^ {0,3}\[(?!\^)[^\]]+\]:\s*(<[^>\n]*>|\S+)")
_HTML_TARGET = re.compile(r"""\b(?:href|src)\s*=\s*(?:"([^"]*)"|'([^']*)')""", re.IGNORECASE)
_SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")


def extract_links(text):
    """Yield (line_number, target) for every link a Markdown renderer would draw from `text`."""
    fence = None
    in_comment = False
    for number, line in enumerate(text.splitlines(), start=1):
        if fence is not None:
            closing = _FENCE.match(line)
            if closing and closing.group(1)[0] == fence[0] and len(closing.group(1)) >= len(fence) \
                    and not line[closing.end():].strip():
                fence = None
            continue
        opening = _FENCE.match(line)
        if opening and not in_comment:
            fence = opening.group(1)
            continue

        visible = []
        rest = line
        while rest:
            if in_comment:
                end = rest.find("-->")
                if end < 0:
                    rest = ""
                    break
                rest = rest[end + 3:]
                in_comment = False
            else:
                start = rest.find("<!--")
                if start < 0:
                    visible.append(rest)
                    break
                visible.append(rest[:start])
                rest = rest[start + 4:]
                in_comment = True
        line = _CODE_SPAN.sub("", "".join(visible))

        for match in _INLINE_TARGET.finditer(line):
            yield number, match.group(1)
        definition = _REFERENCE_DEFINITION.match(line)
        if definition:
            yield number, definition.group(1)
        for match in _HTML_TARGET.finditer(line):
            yield number, match.group(1) if match.group(1) is not None else match.group(2)


def is_relative(target):
    target = target.strip("<>").strip()
    return bool(target) and not target.startswith("#") and not target.startswith("//") \
        and not _SCHEME.match(target)


def check_link(source, target, tracked_files, tracked_dirs):
    """Return None when `target`, linked from the tracked file `source`, resolves; else why not."""
    raw = target.strip("<>").strip()
    path = urllib.parse.unquote(raw.split("#", 1)[0].split("?", 1)[0])
    if not path:
        return None  # `?query` or `#fragment` on the linking file itself
    base = "" if path.startswith("/") else posixpath.dirname(source)
    resolved = posixpath.normpath(posixpath.join(base, path.lstrip("/")))

    if resolved == ".." or resolved.startswith("../"):
        segments = resolved.split("/")
        climbed = len(segments) - len([s for s in segments if s != ".."])
        page = segments[climbed] if len(segments) > climbed else ""
        if climbed == 2 and page in GITHUB_REPO_PAGES:
            return None
        return f"leaves the repository ({resolved}) and is not a GitHub repository page"
    if resolved == ".":
        return None  # the repository root itself
    if resolved in tracked_files or resolved in tracked_dirs:
        return None
    return f"resolves to {resolved}, which is not tracked"


def tracked_directories(tracked_files):
    dirs = set()
    for f in tracked_files:
        parent = posixpath.dirname(f)
        while parent and parent not in dirs:
            dirs.add(parent)
            parent = posixpath.dirname(parent)
    return dirs


def scan(markdown_files, tracked_files):
    """markdown_files: {repo-relative path: text}. Returns (violations, relative link count)."""
    tracked_files = set(tracked_files)
    tracked_dirs = tracked_directories(tracked_files)
    violations = []
    relative = 0
    for source in sorted(markdown_files):
        for number, target in extract_links(markdown_files[source]):
            if not is_relative(target):
                continue
            relative += 1
            reason = check_link(source, target, tracked_files, tracked_dirs)
            if reason:
                violations.append(f"{source}:{number}: ({target}) {reason}")
    return violations, relative


def git_ls_files(repo):
    out = subprocess.run(["git", "-C", repo, "ls-files", "-z"],
                         capture_output=True, check=True).stdout
    return [p for p in out.decode("utf-8").split("\0") if p]


class TrackedMarkdownLinksGuardTests(unittest.TestCase):
    """The guard over the real tree."""

    def test_ShouldResolveEveryRelativeLinkToATrackedPath_WhenScanningTheRepository(self):
        tracked = git_ls_files(_REPO)
        markdown = {}
        for p in tracked:
            if p.lower().endswith(".md") and os.path.isfile(os.path.join(_REPO, p)):
                with open(os.path.join(_REPO, p), encoding="utf-8") as handle:
                    markdown[p] = handle.read()

        violations, relative = scan(markdown, tracked)

        self.assertGreaterEqual(
            len(markdown), MIN_MARKDOWN_FILES,
            f"liveness floor: scanned {len(markdown)} tracked Markdown files; `git ls-files` "
            f"returned {len(tracked)} paths -- is this a git checkout?")
        self.assertGreaterEqual(
            relative, MIN_RELATIVE_LINKS,
            f"liveness floor: found {relative} relative links -- has the extractor stopped "
            "matching?")
        self.assertEqual(
            [], violations,
            "a tracked Markdown file links to a path that is not tracked, so the link is dead for "
            "every reader of the published repository. docs/decisions, docs/plans, "
            "docs/research, docs/specs and openspec are local-only (verbara-meta/ADR-0024), so no "
            "tracked file can link into them:\n  " + "\n  ".join(violations))


class TrackedMarkdownLinksFixtureTests(unittest.TestCase):
    """Fixtures the guard must refuse, and the shapes it must leave alone."""

    TRACKED = [
        "README.md",
        "SECURITY.md",
        "docs/guides/setup.md",
        "docs/guides/provider-wire-conformance.md",
        "docs/guides/assets/diagram.png",
        "src/Verbara.Sdk.Ami/README.md",
    ]

    def _scan(self, source, text):
        return scan({source: text}, self.TRACKED + [source])

    def test_ShouldReportTheFileAndLine_WhenAGuideLinksIntoALocalOnlyDecision(self):
        # The shape #322 stranded eleven times: a public guide linking into docs/decisions/.
        violations, _ = self._scan(
            "docs/guides/setup.md",
            "# Setup\n\nSee [ADR-0048](../decisions/0048-wire-conformance.md) for why.\n")

        self.assertEqual(1, len(violations), violations)
        self.assertIn("docs/guides/setup.md:3:", violations[0])
        self.assertIn("docs/decisions/0048-wire-conformance.md", violations[0])

    def test_ShouldReport_WhenTheTargetIsAReferenceDefinitionOrAnHtmlAttribute(self):
        violations, _ = self._scan(
            "README.md",
            "[plan]: docs/plans/active/2026-09-26-adr-audit-rulings.md\n"
            '<a href="openspec/changes/x/proposal.md">x</a>\n'
            "<img src='docs/research/bench.png'>\n")

        self.assertEqual(
            ["README.md:1:", "README.md:2:", "README.md:3:"],
            [v.split(" ", 1)[0] for v in violations])

    def test_ShouldReport_WhenAnImageOrABadgeLinkPointsAtAnUntrackedFile(self):
        violations, _ = self._scan(
            "README.md",
            "[![diagram](docs/specs/arch.svg)](docs/specs/arch.md)\n")

        self.assertEqual(2, len(violations), violations)

    def test_ShouldReport_WhenTheCaseDiffersFromTheTrackedPath(self):
        # GitHub paths are case-sensitive; a filesystem that is not would hide this one locally.
        violations, _ = self._scan("README.md", "[setup](docs/Guides/setup.md)\n")

        self.assertEqual(1, len(violations), violations)

    def test_ShouldReport_WhenALinkLeavesTheRepositoryForAnotherRepository(self):
        # Three levels up from the root is /<owner>/ on GitHub: a sibling repository, which is
        # private for Pro and meaningless for anyone reading a clone.
        violations, _ = self._scan("README.md", "[pro](../../../Verbara.Sdk.Pro/README.md)\n")

        self.assertEqual(1, len(violations), violations)
        self.assertIn("leaves the repository", violations[0])

    def test_ShouldReport_WhenALinkClimbsTwoLevelsToSomethingThatIsNotAGitHubPage(self):
        violations, _ = self._scan("README.md", "[x](../../Verbara.Sdk.Pro)\n")

        self.assertEqual(1, len(violations), violations)

    def test_ShouldAccept_WhenSecurityLinksToTheGitHubSecurityPage(self):
        violations, _ = self._scan(
            "SECURITY.md", "Report it [privately](../../security/advisories/new).\n")

        self.assertEqual([], violations)

    def test_ShouldAccept_WhenTheTargetResolvesToATrackedFileOrDirectory(self):
        violations, relative = self._scan(
            "src/Verbara.Sdk.Ami/README.md",
            "[setup](../../docs/guides/setup.md#install)\n"
            "[guides](../../docs/guides/)\n"
            "[root](/README.md)\n"
            "[conformance](<../../docs/guides/provider-wire-conformance.md>)\n"
            '<img src="../../docs/guides/assets/diagram.png" alt="d">\n'
            "[spaced](../../docs/guides/set%75p.md)\n")

        self.assertEqual([], violations)
        self.assertEqual(6, relative)

    def test_ShouldIgnore_WhenTheLinkIsAbsoluteAFragmentOrMail(self):
        violations, relative = self._scan(
            "README.md",
            "[site](https://example.com/docs/decisions/x.md)\n"
            "[top](#setup)\n"
            "[mail](mailto:security@example.com)\n"
            "[cdn](//cdn.example.com/x.js)\n")

        self.assertEqual([], violations)
        self.assertEqual(0, relative)

    def test_ShouldIgnore_WhenTheLinkIsInsideCodeOrAComment(self):
        violations, relative = self._scan(
            "README.md",
            "```markdown\n[dead](docs/decisions/0001.md)\n```\n"
            "~~~~\n[dead](docs/decisions/0002.md)\n~~~~\n"
            "Write `[dead](docs/decisions/0003.md)` to link.\n"
            "<!-- [dead](docs/decisions/0004.md) -->\n"
            "<!--\n[dead](docs/decisions/0005.md)\n-->\n")

        self.assertEqual([], violations)
        self.assertEqual(0, relative)

    def test_ShouldIgnore_WhenTheLineIsAFootnoteDefinition(self):
        # `[^el]: [ElevenLabs](https://...)` in the TTS README is a footnote, not a reference
        # definition: its "target" is the first word of the note.
        violations, relative = self._scan(
            "README.md", "[^el]: [Vendor — Models](https://example.com), accessed 2026-09-20.\n")

        self.assertEqual([], violations)
        self.assertEqual(0, relative)

    def test_ShouldResumeScanning_WhenAFenceCloses(self):
        # A fence that never closed in the extractor would silently skip the rest of every file.
        violations, _ = self._scan(
            "README.md",
            "```csharp\nvar x = 1;\n```\n[dead](docs/decisions/0001.md)\n")

        self.assertEqual(["README.md:4:"], [v.split(" ", 1)[0] for v in violations])


if __name__ == "__main__":
    unittest.main()
