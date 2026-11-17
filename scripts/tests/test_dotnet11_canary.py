"""Tests for scripts/ci/dotnet11-canary.py and the contract of .github/workflows/dotnet11-canary.yml.

The canary is not a required check, so nothing on a PR would notice it measuring .NET 10 by mistake, or
passing an AOT publish ILC never ran. These tests run the script in a throwaway git repository with a
fake `dotnet` first on PATH, and read the workflow as text (stdlib only, verbara-meta/ADR-0013).
"""
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import textwrap
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.abspath(os.path.join(_HERE, os.pardir, os.pardir))
_SCRIPT = os.path.join(_REPO, "scripts", "ci", "dotnet11-canary.py")
_WORKFLOW = os.path.join(_REPO, ".github", "workflows", "dotnet11-canary.yml")

# The fake answers from environment variables, so each test states exactly what dotnet "said".
_FAKE_DOTNET = textwrap.dedent("""\
    #!/usr/bin/env python3
    import os, sys
    args = sys.argv[1:]
    if args == ["--list-sdks"]:
        print(os.environ.get("FAKE_SDKS", ""), end="")
        sys.exit(0)
    if args == ["--list-runtimes"]:
        print(os.environ.get("FAKE_RUNTIMES", ""), end="")
        sys.exit(0)
    if args[:1] == ["publish"]:
        with open(os.environ["FAKE_ARGS_FILE"], "w") as handle:
            handle.write("\\n".join(args))
        print(os.environ.get("FAKE_PUBLISH_LOG", ""))
        binary = os.environ.get("FAKE_BINARY")
        if binary:
            os.makedirs(os.path.dirname(binary), exist_ok=True)
            with open(binary, "w") as handle:
                handle.write("#!/bin/sh\\nexit " + os.environ.get("FAKE_SMOKE_RC", "0") + "\\n")
            os.chmod(binary, 0o755)
        sys.exit(int(os.environ.get("FAKE_PUBLISH_RC", "0")))
    sys.exit(97)
""")

_SDK11 = "11.0.100 [/opt/dotnet/sdk]\n"
_RT11 = ("Microsoft.AspNetCore.App 11.0.0 [/opt/dotnet/shared/Microsoft.AspNetCore.App]\n"
         "Microsoft.NETCore.App 11.0.0 [/opt/dotnet/shared/Microsoft.NETCore.App]\n")
_GOOD_PUBLISH = "  Generating native code\n  AotCanary -> /x/publish/\n"


class CanaryScriptTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="dotnet11-canary-")
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        os.makedirs(os.path.join(self.root, "scripts", "ci"))
        shutil.copy(_SCRIPT, os.path.join(self.root, "scripts", "ci", "dotnet11-canary.py"))
        self._write("global.json", '{\n  "sdk": {\n    "version": "10.0.100",\n'
                                   '    "rollForward": "latestFeature"\n  }\n}\n')
        bin_dir = os.path.join(self.root, ".fakebin")
        os.makedirs(bin_dir)
        fake = os.path.join(bin_dir, "dotnet")
        with open(fake, "w", encoding="utf-8") as handle:
            handle.write(_FAKE_DOTNET)
        os.chmod(fake, os.stat(fake).st_mode | stat.S_IXUSR)
        self.env = dict(os.environ, PATH=bin_dir + os.pathsep + os.environ.get("PATH", ""),
                        FAKE_SDKS=_SDK11, FAKE_RUNTIMES=_RT11,
                        FAKE_ARGS_FILE=os.path.join(self.root, ".fakeargs"))

    def _write(self, name, text):
        path = os.path.join(self.root, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)

    def _read(self, name):
        with open(os.path.join(self.root, name), encoding="utf-8") as handle:
            return handle.read()

    def _git_track(self, *names):
        subprocess.run(["git", "init", "-q"], cwd=self.root, check=True)
        subprocess.run(["git", "add", "--", *names], cwd=self.root, check=True)

    def _run(self, *args):
        return subprocess.run([sys.executable, "scripts/ci/dotnet11-canary.py", *args], cwd=self.root,
                              env=self.env, capture_output=True, text=True, check=False)

    # ── pin-sdk ──────────────────────────────────────────────────────────────────────────────────────
    def test_PinSdk_ShouldRewriteGlobalJsonAndPrintBothVersions_WhenOnlyDotnet11IsReachable(self):
        result = self._run("pin-sdk")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ["sdk=11.0.100", "runtime=11.0.0"])
        self.assertEqual(json.loads(self._read("global.json")),
                         {"sdk": {"version": "11.0.100", "rollForward": "disable"}})

    def test_PinSdk_ShouldFailAndLeaveGlobalJson_WhenADotnet10SdkIsAlsoReachable(self):
        self.env["FAKE_SDKS"] = "10.0.404 [/usr/share/dotnet/sdk]\n" + _SDK11
        result = self._run("pin-sdk")
        self.assertEqual(result.returncode, 1)
        self.assertIn("reachable SDK(s) ['10.0.404', '11.0.100']", result.stderr)
        self.assertIn('"10.0.100"', self._read("global.json"))

    def test_PinSdk_ShouldFail_WhenTwoDotnet11SdksAreReachable(self):
        # Two 11.x SDKs: global.json would pin one, but the runner did not give the isolated install asked for.
        self.env["FAKE_SDKS"] = _SDK11 + "11.0.101 [/opt/dotnet/sdk]\n"
        result = self._run("pin-sdk")
        self.assertEqual(result.returncode, 1)
        self.assertIn("['11.0.100', '11.0.101']", result.stderr)

    def test_PinSdk_ShouldFail_WhenTheOnlySdkIsNotMajor11(self):
        self.env["FAKE_SDKS"] = "10.0.404 [/usr/share/dotnet/sdk]\n"
        self.assertEqual(self._run("pin-sdk").returncode, 1)

    def test_PinSdk_ShouldFail_WhenRuntime10IsReachableBesideRuntime11(self):
        # The tests would roll forward onto 11 anyway, but a reachable 10 means the install is not the
        # isolated one the workflow asked for, and the next lane cannot rely on it.
        self.env["FAKE_RUNTIMES"] = _RT11 + "Microsoft.NETCore.App 10.0.15 [/usr/share/dotnet/shared/x]\n"
        result = self._run("pin-sdk")
        self.assertEqual(result.returncode, 1)
        self.assertIn("runtime(s) ['11.0.0', '10.0.15']", result.stderr)

    def test_PinSdk_ShouldFail_WhenNoRuntimeIsListed(self):
        self.env["FAKE_RUNTIMES"] = ""
        self.assertEqual(self._run("pin-sdk").returncode, 1)

    # ── retarget ─────────────────────────────────────────────────────────────────────────────────────
    def test_Retarget_ShouldReplaceEverySingularElement_WhenTrackedFilesTargetNet10(self):
        self._write("Directory.Build.props", "<Project><PropertyGroup>"
                    "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n")
        self._write("tools/A/A.csproj", "<Project><PropertyGroup>\n"
                    "  <TargetFramework>net10.0</TargetFramework>\n</PropertyGroup></Project>\n")
        self._git_track("Directory.Build.props", "tools/A/A.csproj")
        result = self._run("retarget", "net11.0")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("retargeted 2 <TargetFramework> element(s) in 2 tracked file(s) to net11.0",
                      result.stdout)
        self.assertNotIn("net10.0", self._read("Directory.Build.props") + self._read("tools/A/A.csproj"))

    def test_Retarget_ShouldFail_WhenNothingTargetsNet10(self):
        self._write("A.csproj", "<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework>"
                    "</PropertyGroup></Project>\n")
        self._git_track("A.csproj")
        result = self._run("retarget", "net11.0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("no <TargetFramework>net10.0</TargetFramework> found", result.stderr)

    def test_Retarget_ShouldFail_WhenAFileUsesThePluralElement(self):
        self._write("A.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
                    "</PropertyGroup></Project>\n")
        self._write("B.csproj", "<Project><PropertyGroup><TargetFrameworks>net10.0;net8.0"
                    "</TargetFrameworks></PropertyGroup></Project>\n")
        self._git_track("A.csproj", "B.csproj")
        result = self._run("retarget", "net11.0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("B.csproj", result.stderr)

    def test_Retarget_ShouldFail_WhenAPluralElementTargetsNoNet10(self):
        # Not on .NET 10, so the leftover check cannot see it, and not on .NET 11 after the rewrite either.
        self._write("A.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
                    "</PropertyGroup></Project>\n")
        self._write("B.csproj", "<Project><PropertyGroup><TargetFrameworks>netstandard2.0;net8.0"
                    "</TargetFrameworks></PropertyGroup></Project>\n")
        self._git_track("A.csproj", "B.csproj")
        result = self._run("retarget", "net11.0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("<TargetFrameworks> (plural) in ['B.csproj']", result.stderr)

    def test_Retarget_ShouldFail_WhenNet10IsLeftInAnotherSpelling(self):
        # A conditional or spaced element the singular pattern does not match stays on .NET 10.
        self._write("A.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
                    "</PropertyGroup></Project>\n")
        self._write("B.csproj", "<Project><PropertyGroup><TargetFramework> net10.0 </TargetFramework>"
                    "</PropertyGroup></Project>\n")
        self._git_track("A.csproj", "B.csproj")
        result = self._run("retarget", "net11.0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("net10.0 still targeted in ['B.csproj']", result.stderr)

    def test_Retarget_ShouldIgnoreUntrackedFiles_WhenTheyTargetNet10(self):
        self._write("A.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
                    "</PropertyGroup></Project>\n")
        self._write("obj/Stray.csproj", "<Project><PropertyGroup><TargetFramework>net10.0"
                    "</TargetFramework></PropertyGroup></Project>\n")
        self._git_track("A.csproj")
        self.assertEqual(self._run("retarget", "net11.0").returncode, 0)
        self.assertIn("net10.0", self._read("obj/Stray.csproj"))

    def test_Retarget_ShouldExitTwo_WhenTheMonikerIsMalformed(self):
        self.assertEqual(self._run("retarget", "11.0").returncode, 2)

    # ── aot ──────────────────────────────────────────────────────────────────────────────────────────
    def _binary(self):
        return os.path.join(self.root, "tools", "AotCanary", "bin", "Release", "net11.0", "linux-x64",
                            "publish", "AotCanary")

    def test_Aot_ShouldPassAndCountOtherWarnings_WhenIlcRanCleanAndTheBinaryExitsZero(self):
        self.env.update(FAKE_PUBLISH_LOG=_GOOD_PUBLISH + "x.csproj : warning NU1510: pruned\n"
                        "x.csproj : warning NU1510: pruned\n", FAKE_BINARY=self._binary())
        result = self._run("aot", "net11.0")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("aot net11.0: ILC ran, 0 trim/AOT warnings, smoke run exit 0; other warnings: NU1510×2",
                      result.stdout)
        with open(self.env["FAKE_ARGS_FILE"], encoding="utf-8") as handle:
            args = handle.read().split("\n")
        self.assertIn("-p:TreatWarningsAsErrors=false", args)
        self.assertEqual(args[args.index("-r") + 1], "linux-x64")

    def test_Aot_ShouldFail_WhenIlcDidNotRun(self):
        self.env.update(FAKE_PUBLISH_LOG="  AotCanary -> /x/publish/\n", FAKE_BINARY=self._binary())
        result = self._run("aot", "net11.0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("ILC did not run", result.stderr)

    def test_Aot_ShouldFail_WhenIlcReportsAMethodThatWillAlwaysThrow(self):
        self.env.update(FAKE_PUBLISH_LOG=_GOOD_PUBLISH + "  M() will always throw because\n",
                        FAKE_BINARY=self._binary())
        self.assertEqual(self._run("aot", "net11.0").returncode, 1)

    def test_Aot_ShouldFailNamingTheCodes_WhenThereIsATrimOrAotWarning(self):
        self.env.update(FAKE_PUBLISH_LOG=_GOOD_PUBLISH + "a.cs(1,1): warning IL2026: x\n"
                        "b.cs(1,1): warning IL3050: y\n", FAKE_BINARY=self._binary())
        result = self._run("aot", "net11.0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("IL2026, IL3050", result.stderr)

    def test_Aot_ShouldFail_WhenPublishExitsNonZero(self):
        self.env.update(FAKE_PUBLISH_LOG=_GOOD_PUBLISH, FAKE_PUBLISH_RC="1", FAKE_BINARY=self._binary())
        self.assertEqual(self._run("aot", "net11.0").returncode, 1)

    def test_Aot_ShouldFail_WhenNoBinaryWasPublished(self):
        self.env.update(FAKE_PUBLISH_LOG=_GOOD_PUBLISH)
        result = self._run("aot", "net11.0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("published binary not found", result.stderr)

    def test_Aot_ShouldFail_WhenTheBinaryExitsNonZero(self):
        self.env.update(FAKE_PUBLISH_LOG=_GOOD_PUBLISH, FAKE_BINARY=self._binary(), FAKE_SMOKE_RC="3")
        result = self._run("aot", "net11.0")
        self.assertEqual(result.returncode, 1)
        self.assertIn("exited 3", result.stderr)

    def test_Main_ShouldExitTwo_WhenTheSubcommandIsUnknown(self):
        self.assertEqual(self._run("publish").returncode, 2)
        self.assertEqual(self._run().returncode, 2)


class CanaryWorkflowContractTests(unittest.TestCase):
    """The workflow's promises that only a reading can check: never required, always .NET 11 alone."""

    @classmethod
    def setUpClass(cls):
        with open(_WORKFLOW, encoding="utf-8") as handle:
            cls.text = handle.read()
        cls.code = "\n".join(line for line in cls.text.splitlines() if not line.lstrip().startswith("#"))

    def _on_block(self):
        match = re.search(r"^on:\n(?P<body>(?:[ ].*\n|\n)+?)(?=^\S)", self.code + "\nend:\n", re.M)
        self.assertIsNotNone(match, "no top-level `on:` block")
        return match.group("body")

    def test_Workflow_ShouldNeverRunInTheMergeQueue(self):
        self.assertNotIn("merge_group", self.code)

    def test_Workflow_ShouldRunOnPullRequestsOnlyForItsOwnFiles(self):
        on = self._on_block()
        self.assertIn("pull_request:", on)
        self.assertNotIn("pull_request_target", on)
        paths = re.findall(r"^\s+- '([^']+)'$", on.split("pull_request:", 1)[1], re.M)
        self.assertEqual(sorted(paths), [".github/workflows/dotnet11-canary.yml",
                                         "scripts/ci/dotnet11-canary.py"])
        self.assertIn("schedule:", on)
        self.assertIn("workflow_dispatch:", on)

    def test_Workflow_ShouldInstallDotnet11AloneInEveryJob(self):
        jobs = re.findall(r"^  ([a-z][\w-]*):\n", self.code.split("\njobs:\n", 1)[1], re.M)
        self.assertEqual(sorted(jobs), ["aot", "build", "runtime"])
        self.assertEqual(self.code.count("uses: actions/setup-dotnet@"), 3)
        self.assertEqual(self.code.count("DOTNET_INSTALL_DIR: ${{ runner.temp }}/dotnet-11"), 3)
        self.assertEqual(self.code.count("skip-lts-prepass: true"), 3)
        self.assertEqual(self.code.count("dotnet-version: ${{ env.CANARY_DOTNET }}"), 3)
        self.assertRegex(self.code, r"CANARY_DOTNET: '11\.0\.x'")
        self.assertEqual(self.code.count("dotnet11-canary.py pin-sdk"), 3)

    def test_Workflow_ShouldPinBeforeBuildingAndRetargetBeforePublishing_InEveryJob(self):
        body = self.code.split("\njobs:\n", 1)[1]
        for job in re.split(r"^  (?=[a-z][\w-]*:\n)", body, flags=re.M)[1:]:
            setup = job.index("actions/setup-dotnet@")
            pin = job.index("pin-sdk")
            first_dotnet = min(job.index(word) for word in ("dotnet build", "dotnet test", "aot net11.0")
                               if word in job)
            self.assertLess(setup, pin, job.split(":", 1)[0])
            self.assertLess(pin, first_dotnet, job.split(":", 1)[0])
        aot = body[body.index("  aot:"):]
        self.assertLess(aot.index("retarget net11.0"), aot.index("aot net11.0"))

    def test_Workflow_ShouldRollTheTestsForwardOntoRuntime11(self):
        runtime = self.code[self.code.index("  runtime:"):self.code.index("  aot:")]
        self.assertIn("DOTNET_ROLL_FORWARD: Major", runtime)
        self.assertIn("lfs: true", runtime)
        self.assertIn("--filter \"$UNIT_FILTER\"", runtime)
        self.assertIn("Category!=Functional&Category!=Integration&Category!=Realtime&Category!=Spike",
                      self.code)

    def test_Workflow_ShouldNotLetAPipeHideAFailure(self):
        for line in self.code.splitlines():
            if "| tee" in line:
                block = self.code[:self.code.index(line)].rsplit("run: |", 1)[1]
                self.assertIn("set -o pipefail", block, line.strip())

    def test_Workflow_ShouldOnlyReadTheRepository(self):
        self.assertRegex(self.code, r"(?m)^permissions:\n  contents: read\n(?!  )")


if __name__ == "__main__":
    unittest.main()
