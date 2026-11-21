"""Tests of the two gates the release pipeline relies on: check_packages.py and check_tests.py.

Run from the root of the repository:  python -I -m unittest discover -s scripts/ci/tests -v
"""
import io
import os
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

import check_packages  # noqa: E402
import check_tests  # noqa: E402

IDS = ['CodexFlow.Contracts', 'CodexFlow.Core', 'CodexFlow.Orchestration', 'CodexFlow.Gateway.Http', 'CodexFlow.Storage.Sqlite', 'CodexFlow.Storage.EntityFramework']
INTERNAL = {
    'CodexFlow.Contracts': [],
    'CodexFlow.Core': ['CodexFlow.Contracts'],
    'CodexFlow.Orchestration': ['CodexFlow.Contracts', 'CodexFlow.Core'],
    'CodexFlow.Gateway.Http': ['CodexFlow.Orchestration'],
    'CodexFlow.Storage.Sqlite': ['CodexFlow.Orchestration'],
    'CodexFlow.Storage.EntityFramework': ['CodexFlow.Orchestration'],
}
COMMIT = '37365f507fe4dc9eeda131174c167314515e25c1'


def props(version_prefix='0.1.1', suffix=None):
    suffix_line = f'<VersionSuffix>{suffix}</VersionSuffix>' if suffix else ''
    return f'<Project><PropertyGroup><VersionPrefix>{version_prefix}</VersionPrefix>{suffix_line}</PropertyGroup></Project>'


def nupkg(directory, package_id, version, license_expression='MPL-2.0', commit=COMMIT, dependencies=None, readme=True, dll=True, dependency_versions=None):
    dependencies = INTERNAL[package_id] if dependencies is None else dependencies
    versions = dependency_versions or {}
    dependency_xml = ''.join(
        f'<dependency id="{d}" version="{versions.get(d, version)}" exclude="Build,Analyzers" />' for d in dependencies
    ) + '<dependency id="Some.External" version="1.2.3" exclude="Build,Analyzers" />'
    nuspec = (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>'
        f'<id>{package_id}</id><version>{version}</version>'
        f'<license type="expression">{license_expression}</license><readme>README.md</readme>'
        f'<repository type="git" url="https://github.com/iwaitu/codexflow.git" commit="{commit}" />'
        f'<dependencies><group targetFramework="net10.0">{dependency_xml}</group></dependencies>'
        '</metadata></package>'
    )
    path = os.path.join(directory, f'{package_id}.{version}.nupkg')
    with zipfile.ZipFile(path, 'w') as z:
        z.writestr(f'{package_id}.nuspec', nuspec)
        if readme:
            z.writestr('README.md', '# readme')
        if dll:
            z.writestr(f'lib/net10.0/{package_id}.dll', b'MZ')
    return path


class PackageGateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name
        self.props_path = os.path.join(self.dir, 'Directory.Build.props')
        with open(self.props_path, 'w', encoding='utf-8') as f:
            f.write(props())
        self.pkgs = os.path.join(self.dir, 'pkgs')
        os.makedirs(self.pkgs)

    def tearDown(self):
        self.tmp.cleanup()

    def all_good(self, **overrides):
        for package_id in IDS:
            nupkg(self.pkgs, package_id, '0.1.1', **overrides.get(package_id, {}))

    def problems(self, commit=None):
        return check_packages.check(self.pkgs, self.props_path, commit)

    def test_a_correct_set_has_no_problems(self):
        self.all_good()
        self.assertEqual([], self.problems(COMMIT))

    def test_the_version_comes_from_prefix_and_suffix(self):
        with open(self.props_path, 'w', encoding='utf-8') as f:
            f.write(props('0.2.0', 'preview.3'))
        self.assertEqual('0.2.0-preview.3', check_packages.read_version(self.props_path))
        self.assertEqual('0.1.1', check_packages.read_version(self._write_props(props())))

    def _write_props(self, text):
        path = os.path.join(self.dir, 'other.props')
        with open(path, 'w', encoding='utf-8') as f:
            f.write(text)
        return path

    def test_the_command_line_can_print_just_the_version(self):
        import subprocess
        script = os.path.join(os.path.dirname(check_packages.__file__), 'check_packages.py')
        out = subprocess.run([sys.executable, '-I', script, '--print-version', '--props', self._write_props(props('3.4.5', 'rc.1'))],
                             capture_output=True, text=True, check=True).stdout
        self.assertEqual('3.4.5-rc.1', out.strip())

    def test_a_missing_package_is_reported_by_name(self):
        self.all_good()
        os.remove(os.path.join(self.pkgs, 'CodexFlow.Core.0.1.1.nupkg'))
        self.assertTrue(any('CodexFlow.Core' in p and 'missing' in p for p in self.problems()))

    def test_an_unexpected_package_is_reported(self):
        self.all_good()
        nupkg(self.pkgs, 'CodexFlow.Contracts', '0.1.1')  # same file again is fine
        extra = os.path.join(self.pkgs, 'CodexFlow.Local.0.1.1.nupkg')
        with zipfile.ZipFile(extra, 'w') as z:
            z.writestr('CodexFlow.Local.nuspec', '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>CodexFlow.Local</id><version>0.1.1</version></metadata></package>')
        self.assertTrue(any('CodexFlow.Local' in p and 'unexpected' in p for p in self.problems()))

    def test_a_version_that_differs_from_the_props_file_is_reported(self):
        for package_id in IDS:
            nupkg(self.pkgs, package_id, '0.1.2')
        self.assertTrue(any('0.1.2' in p and '0.1.1' in p for p in self.problems()))

    def test_a_wrong_license_is_reported(self):
        self.all_good(**{'CodexFlow.Core': {'license_expression': 'MIT'}})
        self.assertTrue(any('CodexFlow.Core' in p and 'license' in p for p in self.problems()))

    def test_an_internal_dependency_at_another_version_is_reported(self):
        self.all_good(**{'CodexFlow.Gateway.Http': {'dependency_versions': {'CodexFlow.Orchestration': '0.1.0'}}})
        self.assertTrue(any('CodexFlow.Gateway.Http' in p and 'CodexFlow.Orchestration' in p for p in self.problems()))

    def test_a_package_built_from_another_commit_is_reported_only_when_a_commit_is_given(self):
        self.all_good(**{'CodexFlow.Contracts': {'commit': 'a' * 40}})
        self.assertEqual([], self.problems())
        self.assertTrue(any('CodexFlow.Contracts' in p and 'commit' in p for p in self.problems(COMMIT)))

    def test_a_package_without_readme_or_library_is_reported(self):
        self.all_good(**{'CodexFlow.Core': {'readme': False}, 'CodexFlow.Contracts': {'dll': False}})
        problems = self.problems()
        self.assertTrue(any('CodexFlow.Core' in p and 'README' in p for p in problems))
        self.assertTrue(any('CodexFlow.Contracts' in p and '.dll' in p for p in problems))

    def test_an_empty_directory_is_a_problem_not_a_pass(self):
        self.assertTrue(self.problems())


def trx(directory, name, passed, failed=0, not_executed=0, error=0):
    executed = passed + failed + error
    text = (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Completed">'
        f'<Counters total="{executed + not_executed}" executed="{executed}" passed="{passed}" failed="{failed}" error="{error}" '
        f'timeout="0" aborted="0" inconclusive="0" passedButRunAgain="0" notExecuted="{not_executed}" disconnected="0" warning="0" '
        'completed="0" inProgress="0" pending="0" />'
        '</ResultSummary></TestRun>'
    )
    path = os.path.join(directory, name)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(text)
    return path


class TestGateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = self.tmp.name

    def tearDown(self):
        self.tmp.cleanup()

    def test_enough_passing_tests_is_fine_and_skips_do_not_count_against_it(self):
        path = trx(self.dir, 'a.trx', passed=1779, not_executed=13)
        self.assertEqual([], check_tests.check([path], 1700))

    def test_skipped_tests_are_those_counted_in_total_but_not_executed(self):
        path = trx(self.dir, 'a.trx', passed=10, not_executed=3)
        self.assertEqual(3, check_tests.skipped(check_tests._counters(path)))

    def test_a_failure_is_reported(self):
        path = trx(self.dir, 'a.trx', passed=1778, failed=1)
        self.assertTrue(any('failed' in p for p in check_tests.check([path], 1700)))

    def test_an_error_is_reported(self):
        path = trx(self.dir, 'a.trx', passed=1779, error=2)
        self.assertTrue(any('error' in p for p in check_tests.check([path], 1700)))

    def test_fewer_passing_tests_than_the_floor_is_reported(self):
        path = trx(self.dir, 'a.trx', passed=900)
        self.assertTrue(any('1700' in p for p in check_tests.check([path], 1700)))

    def test_results_of_several_files_add_up(self):
        a = trx(self.dir, 'a.trx', passed=1000)
        b = trx(self.dir, 'b.trx', passed=800)
        self.assertEqual([], check_tests.check([a, b], 1700))

    def test_no_result_file_is_a_problem_not_a_pass(self):
        self.assertTrue(check_tests.check([], 1700))
        self.assertTrue(check_tests.check([os.path.join(self.dir, 'missing.trx')], 1700))


if __name__ == '__main__':
    unittest.main()
