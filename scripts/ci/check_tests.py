"""Reads the TRX files of a test run and fails when anything failed or too few tests passed.

    python scripts/ci/check_tests.py --min-passed 1700 TestResults/*.trx

The floor is there so that a run which silently stops finding tests (a broken filter, a project that no longer builds into the run) cannot pass.
Lower it deliberately, in the same commit, when tests are removed on purpose. Skipped tests are reported, not counted as passes.
"""
import argparse
import os
import sys
import xml.etree.ElementTree as ET


def _counters(path):
    root = ET.parse(path).getroot()
    for element in root.iter():
        if element.tag.split('}')[-1] == 'Counters':
            return {key: int(element.get(key, '0')) for key in ('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'notExecuted')}
    return None


def skipped(counters):
    """The TRX of a run through the test platform leaves notExecuted at 0; skipped tests are the ones counted in total but not executed."""
    return counters['total'] - counters['executed']


def check(paths, min_passed):
    problems = []
    if not paths:
        return ['no TRX file was given']
    total = {'passed': 0, 'failed': 0, 'error': 0, 'timeout': 0, 'aborted': 0, 'notExecuted': 0}
    for path in paths:
        if not os.path.isfile(path):
            problems.append(f'{path}: not found')
            continue
        counters = _counters(path)
        if counters is None:
            problems.append(f'{path}: no <Counters> in the file')
            continue
        for key in total:
            total[key] += counters[key]
    if problems:
        return problems
    for key in ('failed', 'error', 'timeout', 'aborted'):
        if total[key]:
            problems.append(f'{total[key]} test(s) {key}')
    if total['passed'] < min_passed:
        problems.append(f'only {total["passed"]} tests passed, the floor is {min_passed}')
    return problems


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--min-passed', type=int, required=True)
    parser.add_argument('trx', nargs='*')
    args = parser.parse_args()
    problems = check(args.trx, args.min_passed)
    if problems:
        print('TEST CHECK FAILED:')
        for problem in problems:
            print('  - ' + problem)
        return 1
    counters = [_counters(p) for p in args.trx]
    print(f'{sum(c["passed"] for c in counters)} passed, {sum(skipped(c) for c in counters)} skipped, none failed (floor {args.min_passed})')
    return 0


if __name__ == '__main__':
    sys.exit(main())
