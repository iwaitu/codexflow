# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Tests for the fixture repositories.

The structural tests always run. The behavioural check (real ``dotnet``, needs a restore of xunit) is opt-in because it is slow:

    DEVSIM_VALIDATE_FIXTURES=1 python -I -m unittest discover -s scripts/e2e/devsim/tests -p "test_fixtures.py"

For every fixture it proves: the baseline builds and its tests pass (the defect is hidden), a proof test FAILS on the defective code, and the
documented fix makes that proof test pass. A fixture that cannot show this would make scenario results meaningless.
"""
from __future__ import annotations

import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

DEVSIM = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(DEVSIM))

import fixtures as f  # noqa: E402

ALLOWED_PACKAGES = {
    "Microsoft.NET.Test.Sdk": "18.0.1",
    "xunit": "2.9.3",
    "xunit.runner.visualstudio": "3.1.5",
}


class StructureTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix="devsim-fixtures-"))
        self.addCleanup(lambda: shutil.rmtree(self.directory, ignore_errors=True))

    def test_there_are_at_least_five_fixtures_with_unique_projects(self):
        self.assertGreaterEqual(len(f.FIXTURES), 5)
        self.assertEqual(len(f.FIXTURES), len({fixture.project for fixture in f.FIXTURES.values()}))

    def test_each_fixture_generates_a_one_commit_repository_with_a_readme_that_states_the_behaviour(self):
        for name in f.FIXTURES:
            path = f.generate(name, self.directory / name)
            fixture = f.FIXTURES[name]
            project = fixture.project
            for relative in (f"{project}.slnx", "README.md", ".gitignore", f"src/{project}/{project}.csproj",
                             f"src/{project}/{fixture.source_file}", f"tests/{project}.Tests/{fixture.test_file}"):
                self.assertTrue((path / relative).is_file(), f"{name}: missing {relative}")
            self.assertIn("Expected behavior", (path / "README.md").read_text(encoding="utf-8"))
            count = subprocess.run(["git", "-C", str(path), "rev-list", "--count", "HEAD"], capture_output=True, text=True).stdout.strip()
            self.assertEqual("1", count, name)
            status = subprocess.run(["git", "-C", str(path), "status", "--porcelain"], capture_output=True, text=True).stdout.strip()
            self.assertEqual("", status, f"{name}: working tree must be clean after generation")

    def test_only_packages_the_offline_restore_cache_holds_are_referenced(self):
        for name in f.FIXTURES:
            path = f.generate(name, self.directory / name)
            for csproj in path.rglob("*.csproj"):
                for package, version in re.findall(r'PackageReference Include="([^"]+)" Version="([^"]+)"', csproj.read_text(encoding="utf-8")):
                    self.assertEqual(ALLOWED_PACKAGES.get(package), version, f"{name}: {package} {version} is not in the offline cache")

    def test_each_defective_fixture_documents_a_fix_that_applies_to_its_own_source(self):
        for name, fixture in f.FIXTURES.items():
            if fixture.fix_old:
                self.assertEqual(1, fixture.source.count(fixture.fix_old), f"{name}: fix_old must occur exactly once")
                self.assertTrue(fixture.proof_test, f"{name}: a defect needs a proof test")

    def test_the_injection_fixture_carries_hostile_text_and_no_defect(self):
        fixture = f.FIXTURES["injected-readme"]
        self.assertIn("spawn_worker", fixture.readme_extra)
        self.assertEqual("", fixture.fix_old)

    def test_generation_refuses_an_existing_directory_and_urls_are_file_urls(self):
        target = self.directory / "x"
        f.generate("discounts", target)
        with self.assertRaises(FileExistsError):
            f.generate("discounts", target)
        self.assertTrue(f.file_url(target).startswith("file:///"))
        self.assertTrue(f.file_url(target).endswith("/"))


@unittest.skipUnless(os.environ.get("DEVSIM_VALIDATE_FIXTURES") == "1" and shutil.which("dotnet"), "set DEVSIM_VALIDATE_FIXTURES=1 (needs dotnet)")
class BehaviourTests(unittest.TestCase):
    def dotnet_test(self, path: Path, project: str) -> subprocess.CompletedProcess:
        return subprocess.run(["dotnet", "test", f"{project}.slnx", "--nologo", "-v", "q"], cwd=path, capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=600)

    def test_baseline_passes_proof_fails_on_the_defect_and_passes_after_the_fix(self):
        for name, fixture in f.FIXTURES.items():
            with self.subTest(fixture=name):
                directory = Path(tempfile.mkdtemp(prefix="devsim-validate-"))
                self.addCleanup(lambda d=directory: shutil.rmtree(d, ignore_errors=True))
                path = f.generate(name, directory / name)
                project = fixture.project
                baseline = self.dotnet_test(path, project)
                self.assertEqual(0, baseline.returncode, f"{name} baseline:\n{baseline.stdout[-1500:]}")
                if not fixture.fix_old:
                    continue
                proof = path / "tests" / f"{project}.Tests" / "ProofTests.cs"
                proof.write_text(fixture.proof_test, encoding="utf-8")
                before = self.dotnet_test(path, project)
                self.assertNotEqual(0, before.returncode, f"{name}: the proof test must fail on the defect")
                source = path / "src" / project / fixture.source_file
                source.write_text(source.read_text(encoding="utf-8").replace(fixture.fix_old, fixture.fix_new), encoding="utf-8")
                after = self.dotnet_test(path, project)
                self.assertEqual(0, after.returncode, f"{name}: the documented fix must make the proof test pass:\n{after.stdout[-1500:]}")


if __name__ == "__main__":
    unittest.main()


class PolyglotFixtureTests(unittest.TestCase):
    """The Node and Python fixtures: same shape as the .NET ones (one commit, a README that states the behaviour, one documented defect)."""

    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix="devsim-polyglot-"))
        self.addCleanup(lambda: shutil.rmtree(self.directory, ignore_errors=True))

    def test_both_languages_are_registered_and_known_to_scenarios(self):
        self.assertEqual({"node", "python"}, {fx.language for fx in f.POLYGLOT_FIXTURES.values()})
        self.assertTrue({"node-slugs", "python-slugs", "node-pager", "python-pager"} <= set(f.known_fixture_names()))
        self.assertTrue(set(f.FIXTURES) <= set(f.known_fixture_names()))

    def test_each_generates_a_clean_one_commit_repository_with_a_readme_that_states_the_behaviour(self):
        for name, fixture in f.POLYGLOT_FIXTURES.items():
            path = f.generate(name, self.directory / name)
            for relative in fixture.files:
                self.assertTrue((path / relative).is_file(), f"{name}: missing {relative}")
            self.assertIn("Expected behavior", (path / "README.md").read_text(encoding="utf-8"))
            self.assertIn("Reproducible defect", (path / "README.md").read_text(encoding="utf-8"))
            count = subprocess.run(["git", "-C", str(path), "rev-list", "--count", "HEAD"], capture_output=True, text=True).stdout.strip()
            self.assertEqual("1", count, name)
            status = subprocess.run(["git", "-C", str(path), "status", "--porcelain"], capture_output=True, text=True).stdout.strip()
            self.assertEqual("", status, f"{name}: working tree must be clean after generation")

    def test_the_documented_fix_applies_exactly_once_to_the_source(self):
        for name, fixture in f.POLYGLOT_FIXTURES.items():
            self.assertEqual(1, fixture.files[fixture.source_file].count(fixture.fix_old), f"{name}: fix_old must occur exactly once")
            self.assertTrue(fixture.proof_test, name)

    def test_the_node_fixture_uses_the_dependencies_the_prepared_npm_cache_holds(self):
        files = f.POLYGLOT_FIXTURES["node-slugs"].files
        shared = Path(f.__file__).resolve().parents[1] / "verification-fixtures" / "node-slugs"
        self.assertEqual((shared / "package.json").read_text(encoding="utf-8"), files["package.json"])
        self.assertEqual((shared / "package-lock.json").read_text(encoding="utf-8"), files["package-lock.json"])

    def test_the_python_pager_defect_is_real_and_the_documented_fix_removes_it(self):
        fixture = f.POLYGLOT_FIXTURES["python-pager"]
        defective: dict = {}
        exec(compile(fixture.files["pager.py"], "pager.py", "exec"), defective)
        self.assertEqual(3, len(defective["paginate"](list(range(10)), 1, 3)))      # the existing tests pass: the defect is hidden
        self.assertEqual([3, 4], defective["paginate"]([1, 2, 3, 4, 5], 1, 2))      # the documented symptom
        fixed: dict = {}
        exec(compile(fixture.files["pager.py"].replace(fixture.fix_old, fixture.fix_new), "pager.py", "exec"), fixed)
        self.assertEqual([1, 2], fixed["paginate"]([1, 2, 3, 4, 5], 1, 2))
        self.assertEqual([5], fixed["paginate"]([1, 2, 3, 4, 5], 3, 2))
        with self.assertRaises(ValueError):
            fixed["paginate"]([1], 0, 2)

    def test_the_python_defect_is_real_and_the_documented_fix_removes_it(self):
        fixture = f.POLYGLOT_FIXTURES["python-slugs"]
        defective: dict = {}
        exec(compile(fixture.files["slug.py"], "slug.py", "exec"), defective)
        self.assertEqual("hello-world", defective["slugify"]("Hello World"))        # the existing tests pass: the defect is hidden
        self.assertEqual("hello---world", defective["slugify"]("Hello   World"))    # the documented symptom
        fixed: dict = {}
        exec(compile(fixture.files["slug.py"].replace(fixture.fix_old, fixture.fix_new), "slug.py", "exec"), fixed)
        self.assertEqual("hello-world", fixed["slugify"]("Hello   World"))
        self.assertEqual("hi", fixed["slugify"]("  --Hi!-- "))


class UpgradeFixtureTests(unittest.TestCase):
    """The dependency-upgrade fixtures: no code defect, one dependency pinned at a version the advisory data flags."""

    def test_both_are_known_to_scenarios_and_pin_the_current_version_in_their_manifest(self):
        self.assertTrue({"node-upgrade", "python-upgrade"} <= set(f.known_fixture_names()))
        for name, fixture in f.UPGRADE_FIXTURES.items():
            directory = Path(f.__file__).resolve().parents[1] / "verification-fixtures" / name
            manifest = (directory / fixture.manifest).read_text(encoding="utf-8")
            self.assertIn(fixture.package, manifest, name)
            self.assertIn(fixture.current_version, manifest, name)
            self.assertNotIn(fixture.target_version, manifest, name)
            self.assertNotIn("vulnerab", (directory / "README.md").read_text(encoding="utf-8").lower(), f"{name}: the host scan must find it, not the README")

    def test_the_python_fixture_generates_a_clean_one_commit_repository(self):
        directory = Path(tempfile.mkdtemp(prefix="devsim-upgrade-"))
        self.addCleanup(lambda: shutil.rmtree(directory, ignore_errors=True))
        path = f.generate("python-upgrade", directory / "python-upgrade")
        self.assertEqual("idna==3.6\n", (path / "requirements.txt").read_text(encoding="utf-8"))
        count = subprocess.run(["git", "-C", str(path), "rev-list", "--count", "HEAD"], capture_output=True, text=True).stdout.strip()
        self.assertEqual("1", count)
