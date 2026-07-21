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
  * the diff ADDS an executable source line but diff-cover measured ZERO lines
    -> exit 1, loud (the report does not line up with the source tree).
A clean diff that adds no measurable executable source is NOT a liveness failure —
there is legitimately nothing to measure; patch coverage passes. This covers not
only docs/config-only changes but also import-path refactors, pure renames, and
comment/type-only or test-only edits: those touch source files yet add no line
diff-cover can score, so a zero measurement is honest, not a mis-wiring. It also
covers a change confined to a coverage-EXCLUDED project — a multi-project repo may
verify DB-bound code (e.g. a *.Storage.Postgres project) only in a separate
integration-tests job and exclude it from the unit-coverage report by design; that
project's changed lines are absent from this report, so the liveness trip scopes to
the changed file's PROJECT (not just its top-level dir) to tell "excluded project"
apart from "mis-wired report".

Exit codes: 0 = pass, 1 = fail (below floor OR a liveness trip OR a wiring error).
"""
import json
import os
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


# An added line matching any of these is never scored by diff-cover, so a diff made
# up ONLY of them (an import-path refactor, a rename, a comment/type edit) yields a
# legitimate zero measurement — not a mis-wired report. Conservative by design: any
# added line that is NOT clearly non-executable counts as executable, so a real code
# change still arms the liveness trip.
_NON_EXECUTABLE_PREFIXES = (
    "import ", "from ", "} from ", "export {", "export type ", "export * ",
    "//", "/*", "*/", "* ",
)
_NON_EXECUTABLE_EXACT = {"*", "{", "}", "(", ")", "[", "]", "};", "),", ");", "],", "})"}


def _is_test_path(path):
    lower = path.lower()
    return (".test." in lower or ".spec." in lower
            or "/tests/" in lower or "/__tests__/" in lower)


def report_source_files(report_path, fmt):
    """The set of file paths the coverage report actually instruments. Used to
    scope the liveness trip: a changed file that is NOT in the report (a config
    file like eslint.config.js, a build script) can never be measured, so its
    executable-looking lines are not a mis-wiring signal."""
    files = set()
    if fmt == "lcov":
        with open(report_path, encoding="utf-8") as handle:
            for line in handle:
                if line.startswith("SF:"):
                    files.add(line[3:].strip())
    else:  # cobertura
        for cls in ET.parse(report_path).getroot().iter("class"):
            filename = cls.get("filename")
            if filename:
                files.add(filename)
    return files


def instrumented_roots(report_path, fmt):
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

    Cobertura paths are normalized to repo-relative before this runs; lcov `SF:`
    paths are already repo-relative. If no root can be derived (a flat report),
    returns None so the caller falls back to the original 'any source file' rule."""
    roots = set()
    for entry in report_source_files(report_path, fmt):
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


def diff_adds_executable_source(merge_base, source_extensions, roots, report_files):
    """True if the PR diff ADDS a plausibly-executable line to a NON-TEST source file
    the report is SUPPOSED to measure — a NEW file in an instrumented project, or an
    existing file already carried in the report. Only then is a zero measurement a
    mis-wiring (real code changed but 0 measured). Everything else is legitimately
    unmeasurable and passes as 'nothing to gate': an import-path refactor, rename,
    comment/type-only or test-only edit (no scorable line at all); a change confined to
    non-instrumented config/tooling or a coverage-excluded project (out of the
    instrumented projects); and a modified file the report does not carry — a pure
    interface/abstract declaration file (its new `Foo();` signature lines look
    executable but produce no coverage), or an integration-only class no unit test
    loads. A NEW file still trips (its executable code must be tested); only an
    already-existing, report-absent file is waved through, so untested new code can
    never hide."""
    proc = run(["git", "diff", "--unified=0", f"{merge_base}...HEAD"])
    if proc.returncode != 0:
        return False
    is_source = False
    added_file = False
    for line in proc.stdout.splitlines():
        if line.startswith("--- "):
            # File header (`--- a/path` or `--- /dev/null`); /dev/null marks a new file.
            added_file = line[4:].strip() == "/dev/null"
            continue
        if line.startswith("+++ "):
            path = line[4:].strip()
            if path.startswith("b/"):
                path = path[2:]
            normalized = "/".join(_split_path(path))
            is_source = (any(path.lower().endswith(ext) for ext in source_extensions)
                         and not _is_test_path(path)
                         and _in_scope(path, roots)
                         and (added_file or normalized in report_files))
            continue
        if not is_source or line.startswith("+++") or not line.startswith("+"):
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

        # Liveness 2: the diff ADDS an executable source line but the report
        # measured zero diff lines -> the report does not line up with the tree.
        # Import-only/rename/comment/test-only diffs add no executable line, so a
        # zero measurement there is honest (handled by the measured_lines==0 branch).
        source_extensions = _SOURCE_EXTENSIONS[fmt]
        roots = instrumented_roots(report_for_diffcover, fmt)
        report_files = {
            "/".join(_split_path(entry))
            for entry in report_source_files(report_for_diffcover, fmt)
        }
        if measured_lines == 0 and diff_adds_executable_source(
                merge_base, source_extensions, roots, report_files):
            fail(f"the diff adds executable {fmt} source lines but diff-cover measured "
                 f"0 diff lines (changed lines seen: {changed_lines}). The coverage "
                 f"report is mis-wired against the source tree — refusing to read "
                 f"false-green.")

        if measured_lines == 0:
            # Honestly nothing measurable (docs/config only). Nothing to gate.
            print(f"Patch coverage: no measurable {fmt} lines in this diff "
                  f"(docs/config-only change). floor {patch_floor}% — n/a, pass.")
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
