#!/usr/bin/env python3
"""Fail CI if coverage of the lines CHANGED in this PR is below the patch floor.

Usage: check-patch-coverage.py <coverage-report> <coverage-floor.json>

  <coverage-report>   merged coverage report. Format is auto-detected from the
                      file name: *.xml -> Cobertura, *.info / lcov* -> lcov.
  <coverage-floor.json>  the committed floor file; reads floor["patch"] (percent).

Primary gate of coverage-gate-v2 (verbara-meta/ADR-0013, clause a): the executable
lines changed vs `git merge-base origin/main HEAD` MUST be >= floor["patch"] %,
measured by the diff-cover library. Patch coverage is monotonic by construction:
every PR tests its own new code, so the aggregate can only climb from added code.

This file is BYTE-IDENTICAL across all repos that adopt ADR-0013. It hardcodes no
repo name and no path; everything repo-specific arrives via argv or the floor file.

Path normalization: ReportGenerator's merged Cobertura carries ABSOLUTE filenames
+ multiple <source> roots; git diff paths are repo-relative. diff-cover only
matches when both are repo-relative, so for Cobertura this script rewrites the
report's filenames relative to the current repo root (generic prefix strip — no
hardcoded path) into a temp copy before handing it to diff-cover. Without this the
report and diff never line up and the run reads a false zero (which the liveness
self-test below then correctly flags loud, but the honest fix is to make them line
up).

Liveness self-tests (transplanted from the SyncFence `MinimumScannedFiles` defense,
Sdk/ADR-0004) — a mis-wired report reads false-green without them:
  * empty / unresolved merge-base (a shallow clone) -> exit 1, loud.
  * the diff ADDS a line the report was SUPPOSED to measure but diff-cover measured
    ZERO lines -> exit 1, loud (the report does not line up with the source tree).
A clean diff that adds no measurable line is NOT a liveness failure — there is
legitimately nothing to measure; patch coverage passes.

What counts as "supposed to be measured" is decided by the REPORT, not by the text
of the added line. For a file the report carries, an added line arms the trip only
when that line NUMBER is one the report instruments; comments, XML doc, attribute
lines and the continuation lines of a multi-line statement are not sequence points
in any build, so a zero measurement there is arithmetic, not mis-wiring. Both
formats state the set directly (Cobertura `<line number=>`, lcov `DA:`), and the
numbers are comparable because both sides are HEAD-side: the report comes from this
job's build+test of HEAD, and `git diff --unified=0 <base>...HEAD` counts added
lines from the HEAD-side `+c` of each `@@` header. Only a file that did NOT exist at
the merge base falls back to a text heuristic — it has no instrumented-line set to
consult, and untested new code is exactly what this trip is for.

Three scopes run ahead of that rule and are unchanged: test paths never arm; a
changed file outside the report's instrumented PROJECT roots never arms (a
build/config file, and a by-design coverage-EXCLUDED project — a multi-project repo
may verify DB-bound code, e.g. a *.Storage.Postgres project, only in a separate
integration-tests job, and scoping to the top-level dir alone would wrongly flag it);
and an already-existing file the report does not carry never arms (a pure
interface/declaration file, an integration-only class no unit test loads).

Exit codes: 0 = pass, 1 = fail (below floor OR a liveness trip OR a wiring error).
"""
import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

# --- Format autodetect -------------------------------------------------------
# Sdk / Pro / Platform emit Cobertura XML; Web emits lcov.info. diff-cover reads
# both natively from the same positional argument, so autodetect drives (a) the
# failure-message wording, (b) which source extensions signal a wiring failure,
# and (c) whether path normalization runs (Cobertura only). Extend the map; no
# other change is needed for a new format.
_FORMAT_BY_SUFFIX = {
    ".xml": "cobertura",
    ".info": "lcov",
}

# Measurable source extensions per format. A diff that changes one of these but
# yields zero measured diff lines is the mis-wired-report signature.
_SOURCE_EXTENSIONS = {
    "cobertura": (".cs",),
    "lcov": (".ts", ".tsx", ".js", ".jsx"),
}


def fail(message):
    """One-line, direction-unambiguous. `::error::` makes it a GitHub annotation."""
    print(f"::error::patch-coverage: {message}")
    sys.exit(1)


def detect_format(report_path):
    lower = os.path.basename(report_path).lower()
    for suffix, fmt in _FORMAT_BY_SUFFIX.items():
        if lower.endswith(suffix):
            return fmt
    if "lcov" in lower:
        return "lcov"
    fail(f"cannot autodetect coverage format from report name '{report_path}' "
         f"(expected a *.xml Cobertura or *.info/lcov report).")


def run(cmd, **kwargs):
    return subprocess.run(cmd, capture_output=True, text=True, **kwargs)


def resolve_merge_base():
    """The merge-base of origin/main and HEAD, or None if it cannot be resolved.

    An unresolved merge-base is the shallow-clone signature: `fetch-depth: 0` is
    required (ADR-0013) precisely so this returns a real commit on CI.
    """
    proc = run(["git", "merge-base", "origin/main", "HEAD"])
    if proc.returncode != 0:
        return None
    return proc.stdout.strip() or None


# Last-resort text heuristic, used for ONE case only: a file that did not exist at
# the merge base and that the report does not carry, so no instrumented-line set
# exists to ask. Conservative by design — any added line that is NOT clearly
# non-executable counts as executable, so untested new code still arms the trip.
# For every other file the report itself is the authority (report_instrumented_lines),
# which is why this list never needed to learn C# attribute or continuation lines.
_NON_EXECUTABLE_PREFIXES = (
    "import ", "from ", "} from ", "export {", "export type ", "export * ",
    "//", "/*", "*/", "* ",
)
_NON_EXECUTABLE_EXACT = {"*", "{", "}", "(", ")", "[", "]", "};", "),", ");", "],", "})"}

# `@@ -a,b +c,d @@` — group 1 is the HEAD-side first line of the hunk, which seeds
# the counter that gives every added line its number.
_HUNK_HEADER = re.compile(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")


def _is_test_path(path):
    lower = path.lower()
    return (".test." in lower or ".spec." in lower
            or "/tests/" in lower or "/__tests__/" in lower)


def report_instrumented_lines(report_path, fmt):
    """`{repo-relative path -> {line numbers the report instruments}}`, the gate's
    ground truth in two places at once.

    The KEYS say which files the report measures at all: a changed file that is
    absent (a config file like eslint.config.js, a build script, a pure interface
    declaration, a by-design coverage-excluded project) can never be measured, so
    its executable-LOOKING lines are not a mis-wiring signal. The VALUES say which
    lines of a carried file are sequence points, which is the question the liveness
    trip actually needs answered and the one a text heuristic cannot answer: a C#
    `[LoggerMessage(...)]` attribute line or the continuation line of a multi-line
    statement reads as code and is instrumented by nothing.

    Both formats state it directly — Cobertura as `<line number="N">` under each
    `<class filename=...>`, lcov as `DA:<line>,<hits>` between `SF:` and
    `end_of_record` — so nothing here guesses. Cobertura filenames are normalized to
    repo-relative before this runs; lcov `SF:` paths already are. A merged report may
    carry one file in several `<class>` elements (partial classes, multiple
    packages), so line sets accumulate rather than replace."""
    files = {}
    if fmt == "lcov":
        current = None
        with open(report_path, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if line.startswith("SF:"):
                    key = "/".join(_split_path(line[3:].strip()))
                    current = files.setdefault(key, set())
                elif line == "end_of_record":
                    current = None
                elif line.startswith("DA:") and current is not None:
                    number = line[3:].split(",")[0].strip()
                    if number.isdigit():
                        current.add(int(number))
    else:  # cobertura
        for cls in ET.parse(report_path).getroot().iter("class"):
            filename = cls.get("filename")
            if not filename:
                continue
            entry = files.setdefault("/".join(_split_path(filename)), set())
            for line_element in cls.iter("line"):
                number = line_element.get("number")
                if number and number.isdigit():
                    entry.add(int(number))
    return files


def instrumented_roots(line_map):
    """The set of project-level roots the coverage report instruments (e.g.
    {'src/CoreLib', 'src/DataLayer'} for a multi-project repo, or {'src'} for a flat
    one). Used to scope the liveness trip: a changed executable file whose PROJECT the
    report instruments but leaves unmeasured is a mis-wiring signal; a change to a file
    outside those projects can never be measured by this report, so its
    executable-looking lines are not — this covers both a build/config file
    (eslint.config.js, vite.config.ts) AND a by-design coverage-excluded project (a
    *.Storage.Postgres project verified only by the integration-tests job). Scoping to
    the top-level dir alone (e.g. 'src') would wrongly flag the latter, since the
    excluded project still lives under 'src'.

    Takes the map from report_instrumented_lines, whose keys are already normalized.
    If no root can be derived (a flat report), returns None so the caller falls back
    to the original 'any source file' rule."""
    roots = set()
    for entry in line_map:
        root = _project_root(_split_path(entry))
        if root:
            roots.add(root)
    return roots or None


def _split_path(path):
    """Normalized ('./src/x' and 'src\\x' -> ['src', 'x']) path segments."""
    normalized = os.path.normpath(path.replace("\\", "/")).replace(os.sep, "/")
    return normalized.lstrip("/").split("/")


def _project_root(parts):
    """The project-level prefix of a split path: 'src/Project' for a src/<project>/...
    layout (>=3 segments), 'src' for a flat src/file layout (2 segments), None if too
    shallow to place. This is the granularity at which coverage instrumentation is
    decided: a repo with many build units excludes some projects from the unit-coverage
    report by design (DB-bound integration-only code), so the liveness trip must scope
    to the changed file's PROJECT, not just its shared top-level dir."""
    if len(parts) >= 3:
        return "/".join(parts[:2])
    if len(parts) == 2:
        return parts[0]
    return None


def _in_scope(changed_path, roots):
    if roots is None:  # flat report: keep the original conservative behavior.
        return True
    return _project_root(_split_path(changed_path)) in roots


def diff_adds_executable_source(merge_base, source_extensions, roots, line_map):
    """True if the PR diff ADDS a line to a NON-TEST source file that the report was
    SUPPOSED to measure. Only then is a zero measurement a mis-wiring (something
    measurable changed but 0 measured).

    For a file the report CARRIES, the report decides: the added line arms the trip
    only if its NUMBER is one the report instruments. That is the whole point — a
    comment, an XML doc line, a C# attribute line and the continuation lines of a
    multi-line statement are not sequence points, so measuring 0 for a diff made only
    of those is arithmetic, not mis-wiring, and a text heuristic cannot tell the
    difference. The numbers line up because both sides are HEAD-side (see the module
    docstring).

    Only a file that did NOT exist at the merge base AND is absent from the report
    falls back to the conservative text heuristic: there is no instrumented-line set
    to consult, and untested new code is exactly what this trip is for, so it still
    arms.

    Everything else is legitimately unmeasurable and passes as 'nothing to gate': a
    test-only edit; a change confined to non-instrumented config/tooling or to a
    coverage-excluded project (outside the instrumented project roots); and a modified
    file the report does not carry — a pure interface/abstract declaration file (its
    new `Foo();` signature lines look executable but produce no coverage), or an
    integration-only class no unit test loads."""
    proc = run(["git", "diff", "--unified=0", f"{merge_base}...HEAD"])
    if proc.returncode != 0:
        return False
    is_source = False
    added_file = False
    instrumented = None   # the carried file's instrumented lines, or None if absent
    new_line = 0          # HEAD-side number of the next added line
    for line in proc.stdout.splitlines():
        if line.startswith("--- "):
            # File header (`--- a/path` or `--- /dev/null`); /dev/null marks a new file.
            added_file = line[4:].strip() == "/dev/null"
            continue
        if line.startswith("+++ "):
            path = line[4:].strip()
            if path.startswith("b/"):
                path = path[2:]
            instrumented = line_map.get("/".join(_split_path(path)))
            is_source = (any(path.lower().endswith(ext) for ext in source_extensions)
                         and not _is_test_path(path)
                         and _in_scope(path, roots)
                         and (added_file or instrumented is not None))
            new_line = 0
            continue
        header = _HUNK_HEADER.match(line)
        if header:
            new_line = int(header.group(1))
            continue
        if line.startswith("+++") or not line.startswith("+"):
            # A `-` line consumes no HEAD-side number, so the counter does not move.
            continue
        number = new_line
        new_line += 1
        if not is_source:
            continue
        if instrumented is not None:
            if number in instrumented:
                return True
            continue
        code = line[1:].strip()
        if not code or code in _NON_EXECUTABLE_EXACT:
            continue
        if any(code.startswith(prefix) for prefix in _NON_EXECUTABLE_PREFIXES):
            continue
        return True
    return False


def normalize_cobertura_paths(report_path, out_path):
    """Rewrite absolute Cobertura filenames to repo-relative (relative to CWD) so
    diff-cover can line them up with repo-relative git-diff paths. Strips the
    longest matching <source> root, then collapses <sources> to a single '.'.
    Repo-agnostic: the only anchor is os.getcwd()."""
    tree = ET.parse(report_path)
    root = tree.getroot()
    cwd = os.getcwd()
    source_roots = sorted(
        (s.text for s in root.iter("source") if s.text),
        key=len,
        reverse=True,
    )
    for cls in root.iter("class"):
        filename = cls.get("filename")
        if not filename or not os.path.isabs(filename):
            continue
        new_name = None
        for src_root in source_roots:
            src_root_norm = src_root if src_root.endswith(os.sep) else src_root + os.sep
            if filename.startswith(src_root_norm):
                rel_root = os.path.relpath(src_root, cwd)
                new_name = os.path.join(rel_root, filename[len(src_root_norm):])
                break
        if new_name is None:
            # Fall back to a plain repo-root strip if it lives under CWD.
            new_name = os.path.relpath(filename, cwd)
        cls.set("filename", new_name.replace(os.sep, "/"))
    for sources in root.iter("sources"):
        for child in list(sources):
            sources.remove(child)
        ET.SubElement(sources, "source").text = "."
    tree.write(out_path)


def main():
    if len(sys.argv) != 3:
        fail("usage: check-patch-coverage.py <coverage-report> <coverage-floor.json>")

    report_path, floor_path = sys.argv[1], sys.argv[2]

    if not os.path.isfile(report_path):
        fail(f"coverage report '{report_path}' not found.")
    if not os.path.isfile(floor_path):
        fail(f"floor file '{floor_path}' not found.")

    fmt = detect_format(report_path)

    with open(floor_path, encoding="utf-8") as handle:
        floor_doc = json.load(handle)
    if "patch" not in floor_doc:
        fail(f"floor file '{floor_path}' has no \"patch\" key.")
    patch_floor = float(floor_doc["patch"])

    # Liveness 1: shallow clone / unresolved merge-base.
    merge_base = resolve_merge_base()
    if merge_base is None:
        fail("merge-base of origin/main..HEAD is empty/unresolved — this is a "
             "shallow clone. Set fetch-depth: 0 on the coverage checkout "
             "(ADR-0013). Refusing to read false-green.")

    temp_files = []
    try:
        # For Cobertura, hand diff-cover a path-normalized copy so its absolute
        # filenames line up with the repo-relative git diff.
        if fmt == "cobertura":
            report_for_diffcover = _tempname(temp_files, ".xml")
            try:
                normalize_cobertura_paths(report_path, report_for_diffcover)
            except ET.ParseError as exc:
                fail(f"cannot parse Cobertura report '{report_path}': {exc}")
        else:
            report_for_diffcover = report_path

        # Freeze the exact diff so diff-cover and the liveness probe agree on scope.
        diff_path = _tempname(temp_files, ".diff")
        diff_proc = run(["git", "diff", f"{merge_base}...HEAD"])
        if diff_proc.returncode != 0:
            fail(f"`git diff {merge_base}...HEAD` failed: {diff_proc.stderr.strip()}")
        with open(diff_path, "w", encoding="utf-8") as handle:
            handle.write(diff_proc.stdout)

        json_path = _tempname(temp_files, ".json")
        dc = run([
            "diff-cover", report_for_diffcover,
            "--diff-file", diff_path,
            "--src-roots", ".",
            "--format", f"json:{json_path}",
        ])
        # diff-cover exits 1 when coverage < an internal --fail-under; we do NOT
        # pass --fail-under (we own the comparison), so a nonzero exit here with
        # no parseable JSON is a real tool failure.
        try:
            with open(json_path, encoding="utf-8") as handle:
                result = json.load(handle)
        except (OSError, json.JSONDecodeError):
            fail(f"diff-cover produced no parseable report (exit {dc.returncode}). "
                 f"stderr: {dc.stderr.strip()}")

        measured_lines = int(result.get("total_num_lines", 0))
        changed_lines = int(result.get("num_changed_lines", 0))

        # Liveness 2: the diff ADDS a line the report instruments but the report
        # measured zero diff lines -> the report does not line up with the tree.
        # A diff of comments/attributes/continuations, or of files the report does
        # not carry, adds no instrumented line, so a zero measurement there is
        # honest (handled by the measured_lines==0 branch below).
        source_extensions = _SOURCE_EXTENSIONS[fmt]
        line_map = report_instrumented_lines(report_for_diffcover, fmt)
        roots = instrumented_roots(line_map)
        if measured_lines == 0 and diff_adds_executable_source(
                merge_base, source_extensions, roots, line_map):
            fail(f"the diff adds instrumented {fmt} source lines but diff-cover measured "
                 f"0 diff lines (changed lines seen: {changed_lines}). The coverage "
                 f"report is mis-wired against the source tree — refusing to read "
                 f"false-green.")

        if measured_lines == 0:
            # Honestly nothing measurable — the diff adds no line this report
            # instruments (docs/config, comments/attributes, an uninstrumented
            # project). Nothing to gate.
            print(f"Patch coverage: no measurable {fmt} lines in this diff "
                  f"(no instrumented line added). floor {patch_floor}% — n/a, pass.")
            return

        patch_pct = round(float(result.get("total_percent_covered", 0.0)), 2)
        covered = measured_lines - int(result.get("total_num_violations", 0))
        print(f"Patch coverage: {patch_pct}%  "
              f"({covered}/{measured_lines} changed executable lines, "
              f"floor {patch_floor}%, format {fmt})")

        if patch_pct < patch_floor:
            fail(f"patch coverage {patch_pct}% is below the floor {patch_floor}%. "
                 f"Add tests for the changed lines in this PR.")
        print("Patch coverage OK.")
    finally:
        for path in temp_files:
            _unlink(path)


def _tempname(registry, suffix):
    handle = tempfile.NamedTemporaryFile("w", suffix=suffix, delete=False)
    handle.close()
    registry.append(handle.name)
    return handle.name


def _unlink(path):
    try:
        os.unlink(path)
    except OSError:
        pass


if __name__ == "__main__":
    main()
