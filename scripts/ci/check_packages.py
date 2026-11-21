"""Checks the packed .nupkg files before anything is published.

    python scripts/ci/check_packages.py --packages <dir> --props Directory.Build.props [--commit <sha>]
    python scripts/ci/check_packages.py --print-version --props Directory.Build.props

Exit 0 when the directory holds exactly the five library packages, each at the version Directory.Build.props declares, under MPL-2.0, with its
README and its library, depending on each other at that same version (and, when --commit is given, built from that commit). Exit 1 and a list
of problems otherwise. An empty directory is a failure, never a pass.
"""
import argparse
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

EXPECTED = ['CodexFlow.Contracts', 'CodexFlow.Core', 'CodexFlow.Orchestration', 'CodexFlow.Gateway.Http', 'CodexFlow.Storage.Sqlite', 'CodexFlow.Storage.EntityFramework']
LICENSE = 'MPL-2.0'


def read_version(props_path):
    root = ET.parse(props_path).getroot()
    prefix = suffix = None
    for element in root.iter():
        tag = element.tag.split('}')[-1]
        if tag == 'VersionPrefix' and element.text:
            prefix = element.text.strip()
        elif tag == 'VersionSuffix' and element.text:
            suffix = element.text.strip()
    if not prefix:
        raise SystemExit(f'no <VersionPrefix> in {props_path}')
    return f'{prefix}-{suffix}' if suffix else prefix


def _text(metadata, name):
    for element in metadata:
        if element.tag.split('}')[-1] == name:
            return element
    return None


def _read(path):
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        nuspec_name = next((n for n in names if n.endswith('.nuspec') and '/' not in n), None)
        if nuspec_name is None:
            return None, names
        return ET.fromstring(z.read(nuspec_name)), names


def check(packages_dir, props_path, commit=None):
    problems = []
    version = read_version(props_path)
    files = sorted(glob.glob(os.path.join(packages_dir, '*.nupkg')))
    if not files:
        return [f'no .nupkg in {packages_dir}']

    found = {}
    for path in files:
        try:
            root, names = _read(path)
        except zipfile.BadZipFile:
            problems.append(f'{os.path.basename(path)}: not a valid package')
            continue
        if root is None:
            problems.append(f'{os.path.basename(path)}: no .nuspec')
            continue
        metadata = _text(root, 'metadata')
        package_id = _text(metadata, 'id').text
        found[package_id] = (path, metadata, names)

    for package_id in found:
        if package_id not in EXPECTED:
            problems.append(f'unexpected package {package_id}')
    for package_id in EXPECTED:
        if package_id not in found:
            problems.append(f'{package_id}: missing')

    for package_id in EXPECTED:
        if package_id not in found:
            continue
        path, metadata, names = found[package_id]
        actual = _text(metadata, 'version').text
        if actual != version:
            problems.append(f'{package_id}: version {actual} but {os.path.basename(props_path)} says {version}')

        license_element = _text(metadata, 'license')
        if license_element is None or (license_element.text or '').strip() != LICENSE:
            problems.append(f'{package_id}: license is not {LICENSE}')

        readme = _text(metadata, 'readme')
        if readme is None or (readme.text or '').strip() not in names:
            problems.append(f'{package_id}: README is not in the package')

        if f'lib/net10.0/{package_id}.dll' not in names:
            problems.append(f'{package_id}: lib/net10.0/{package_id}.dll is not in the package')

        if commit:
            repository = _text(metadata, 'repository')
            built_from = repository.get('commit') if repository is not None else None
            if built_from != commit:
                problems.append(f'{package_id}: built from commit {built_from}, expected {commit}')

        for dependency in metadata.iter():
            if dependency.tag.split('}')[-1] != 'dependency':
                continue
            dependency_id = dependency.get('id')
            if dependency_id in EXPECTED and dependency.get('version') != version:
                problems.append(f'{package_id}: depends on {dependency_id} {dependency.get("version")}, expected {version}')

    return problems


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--packages')
    parser.add_argument('--props', default='Directory.Build.props')
    parser.add_argument('--commit', default=None)
    parser.add_argument('--print-version', action='store_true', help='print the version Directory.Build.props declares and stop')
    args = parser.parse_args()
    if args.print_version:
        print(read_version(args.props))
        return 0
    if not args.packages:
        parser.error('--packages is required')
    if args.commit and not re.fullmatch(r'[0-9a-f]{40}', args.commit):
        raise SystemExit(f'--commit must be a full 40-character sha, got {args.commit!r}')
    problems = check(args.packages, args.props, args.commit)
    if problems:
        print('PACKAGE CHECK FAILED:')
        for problem in problems:
            print('  - ' + problem)
        return 1
    print(f'{len(EXPECTED)} packages at {read_version(args.props)}: license, README, library and internal dependencies are consistent'
          + (f', built from {args.commit}' if args.commit else ''))
    return 0


if __name__ == '__main__':
    sys.exit(main())
