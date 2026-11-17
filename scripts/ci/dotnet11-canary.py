#!/usr/bin/env python3
"""The .NET 11 canary's three steps that are more than one command (.github/workflows/dotnet11-canary.yml).

The canary answers one question before anyone decides on a `net11.0` target: does this tree build,
run and publish Native AOT on .NET 11 today? It is not a required check and never blocks a merge.
Each subcommand fails closed, because a canary that quietly measured .NET 10 reads exactly like a
canary that found nothing:

  pin-sdk          Require exactly one SDK and one Microsoft.NETCore.App runtime reachable, both of
                   major 11, and rewrite global.json to that SDK. The repository's global.json says
                   10.0.100 with `latestFeature`, which never crosses a major, so without the rewrite
                   the SDK 11 install is refused. Prints `sdk=` and `runtime=` for $GITHUB_OUTPUT.
  retarget TFM     Replace every `<TargetFramework>net10.0</TargetFramework>` in the tracked
                   *.csproj, *.props and *.targets files with TFM. Fails when it replaced nothing,
                   when one is left, or when a file uses `<TargetFrameworks>` (plural), which this
                   rewrite does not handle and would otherwise leave on .NET 10 unnoticed.
  aot TFM          Publish tools/AotCanary for linux-x64 with ILC and judge the output the way
                   tools/verify-aot.sh does (ILC must have run; no "will always throw"), plus every
                   trim/AOT analysis warning (ILxxxx) fails. Other warnings are counted, not judged:
                   the build lane owns them. The published binary must run and exit 0.

Stdlib only (verbara-meta/ADR-0013). Exit codes: 0 pass, 1 the check failed, 2 usage or setup.
"""
import json
import os
import re
import shutil
import subprocess
import sys

_REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, os.pardir))
_MAJOR = "11"
_AOT_PROJECT = "tools/AotCanary/AotCanary.csproj"
_RID = "linux-x64"

_SINGULAR = re.compile(r"<TargetFramework>net10\.0</TargetFramework>")
_ANY_NET10 = re.compile(r"<TargetFrameworks?>[^<]*\bnet10\.0\b[^<]*</TargetFrameworks?>")
_PLURAL = re.compile(r"<TargetFrameworks>")
_TFM = re.compile(r"^net[0-9]+\.[0-9]+$")
_IL_WARNING = re.compile(r"\bwarning (IL[0-9]{4})\b")
_ANY_WARNING = re.compile(r"\bwarning ([A-Z]+[0-9]+)\b")


def fail(message, code=1):
    print(f"dotnet11-canary: {message}", file=sys.stderr)
    return code


def _dotnet(*args):
    return subprocess.run(["dotnet", *args], cwd=_REPO, capture_output=True, text=True, check=False)


def _listed(output, column):
    """`dotnet --list-sdks` / `--list-runtimes` rows → the version column of the matching rows."""
    rows = []
    for line in output.splitlines():
        parts = line.split()
        if column is None and parts:
            rows.append(parts[0])
        elif column is not None and len(parts) >= 2 and parts[0] == column:
            rows.append(parts[1])
    return rows


def pin_sdk():
    sdks = _dotnet("--list-sdks")
    runtimes = _dotnet("--list-runtimes")
    if sdks.returncode != 0 or runtimes.returncode != 0:
        return fail("`dotnet --list-sdks` or `--list-runtimes` failed; is dotnet on PATH?", 2)
    sdk_versions = _listed(sdks.stdout, None)
    runtime_versions = _listed(runtimes.stdout, "Microsoft.NETCore.App")
    if len(sdk_versions) != 1 or not sdk_versions[0].startswith(_MAJOR + "."):
        return fail(f"reachable SDK(s) {sdk_versions}; the canary needs exactly one, of major {_MAJOR}")
    if len(runtime_versions) != 1 or not runtime_versions[0].startswith(_MAJOR + "."):
        return fail(f"reachable Microsoft.NETCore.App runtime(s) {runtime_versions}; "
                    f"the canary needs exactly one, of major {_MAJOR}")
    path = os.path.join(_REPO, "global.json")
    with open(path, encoding="utf-8") as handle:
        document = json.load(handle)
    document.setdefault("sdk", {})
    document["sdk"]["version"] = sdk_versions[0]
    document["sdk"]["rollForward"] = "disable"
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(document, handle, indent=2)
        handle.write("\n")
    print(f"sdk={sdk_versions[0]}")
    print(f"runtime={runtime_versions[0]}")
    return 0


def _tracked_project_files():
    listed = subprocess.run(["git", "ls-files", "-z", "--", "*.csproj", "*.props", "*.targets"],
                            cwd=_REPO, capture_output=True, check=False)
    if listed.returncode != 0:
        return None
    return [name for name in listed.stdout.decode("utf-8").split("\0") if name]


def retarget(tfm):
    if not _TFM.match(tfm):
        return fail(f"'{tfm}' is not a target framework moniker like net11.0", 2)
    files = _tracked_project_files()
    if files is None:
        return fail("`git ls-files` failed; run from a git checkout", 2)
    replaced, plural = 0, []
    for name in files:
        path = os.path.join(_REPO, name)
        with open(path, encoding="utf-8-sig") as handle:
            text = handle.read()
        if _PLURAL.search(text):
            plural.append(name)
        new_text, count = _SINGULAR.subn(f"<TargetFramework>{tfm}</TargetFramework>", text)
        if count:
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(new_text)
            replaced += count
    if plural:
        return fail(f"<TargetFrameworks> (plural) in {plural}; this rewrite handles the singular form only")
    if replaced == 0:
        return fail("no <TargetFramework>net10.0</TargetFramework> found to replace")
    left = []
    for name in files:
        with open(os.path.join(_REPO, name), encoding="utf-8-sig") as handle:
            if _ANY_NET10.search(handle.read()):
                left.append(name)
    if left:
        return fail(f"net10.0 still targeted in {left}")
    print(f"retargeted {replaced} <TargetFramework> element(s) in {len(files)} tracked file(s) to {tfm}")
    return 0


def aot(tfm):
    if not _TFM.match(tfm):
        return fail(f"'{tfm}' is not a target framework moniker like net11.0", 2)
    # ILC is incremental: with its intermediates present and nothing changed it prints nothing.
    shutil.rmtree(os.path.join(_REPO, "tools", "AotCanary", "obj", "Release", tfm, _RID, "native"),
                  ignore_errors=True)
    published = _dotnet("publish", _AOT_PROJECT, "-c", "Release", "-r", _RID, "--self-contained",
                        "--nologo", "-p:TreatWarningsAsErrors=false")
    log = published.stdout + published.stderr
    print(log)
    if published.returncode != 0:
        return fail(f"dotnet publish exited {published.returncode}")
    if "Generating native code" not in log:
        return fail("ILC did not run (no 'Generating native code' line), so the publish proves nothing")
    if "will always throw" in log:
        return fail("ILC compiled a method that will always throw")
    il_warnings = sorted(set(_IL_WARNING.findall(log)))
    if il_warnings:
        return fail(f"trim/AOT analysis warning(s): {', '.join(il_warnings)}")
    others = {}
    for code in _ANY_WARNING.findall(log):
        others[code] = others.get(code, 0) + 1
    binary = os.path.join(_REPO, "tools", "AotCanary", "bin", "Release", tfm, _RID, "publish", "AotCanary")
    if not os.access(binary, os.X_OK):
        return fail(f"published binary not found at {os.path.relpath(binary, _REPO)}")
    smoke = subprocess.run([binary], cwd=_REPO, capture_output=True, text=True, check=False)
    print(smoke.stdout + smoke.stderr)
    if smoke.returncode != 0:
        return fail(f"the published binary exited {smoke.returncode}")
    counted = ", ".join(f"{code}×{n}" for code, n in sorted(others.items())) or "none"
    print(f"aot {tfm}: ILC ran, 0 trim/AOT warnings, smoke run exit 0; other warnings: {counted}")
    return 0


def main(argv):
    if argv[:1] == ["pin-sdk"] and len(argv) == 1:
        return pin_sdk()
    if argv[:1] == ["retarget"] and len(argv) == 2:
        return retarget(argv[1])
    if argv[:1] == ["aot"] and len(argv) == 2:
        return aot(argv[1])
    return fail("usage: dotnet11-canary.py pin-sdk | retarget <tfm> | aot <tfm>", 2)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
