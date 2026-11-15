#!/usr/bin/env python3
# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Prepares the offline caches the Node and Python host verifiers restore from, and runs the real-container tests.

The verification containers have no network, so the host has to hand them an npm cache and a wheelhouse. This script
fills both for the small fixture projects in scripts/e2e/verification-fixtures (it downloads from npm and PyPI, once, in
throw-away containers that do have a network) and prints the environment variables the tests need.

    python scripts/e2e/prepare-verification-caches.py --out ./verification-caches
    python scripts/e2e/prepare-verification-caches.py --out ./verification-caches --run-tests

Requires Docker and the images node:22-alpine and python:3.12-alpine (pulled when missing).
"""
import argparse
import os
import subprocess
import sys

NODE_IMAGE = "node:22-alpine"
PY_IMAGE = "python:3.12-alpine"


def run(*args, env=None):
    print("+", " ".join(args), flush=True)
    subprocess.run(args, check=True, env=env)


# The dependency-upgrade fixtures (scripts/e2e/devsim/fixtures.py, UPGRADE_FIXTURES): the package each one has to raise and the
# version the advisory data names as the first fixed one. The caches must hold BOTH versions: the current one for the baseline
# restore, the target for the worker's offline lockfile update and for the host verification.
NODE_UPGRADE = ("node-upgrade", "minimist", "1.2.5", "1.2.6")
PY_UPGRADE = ("python-upgrade", "idna", "3.6", "3.15")

NPM = "npm --cache /cache --ignore-scripts --no-audit --no-fund"


def prepare_upgrade_fixtures(fixtures: str, npm_cache: str, wheels: str, env: dict) -> None:
    name, package, current, target = NODE_UPGRADE
    fixture = os.path.join(fixtures, name)
    lock = os.path.join(fixture, "package-lock.json")
    if not os.path.isfile(lock):
        # The lockfile is generated once, by npm, from the node-slugs lockfile (so Jest stays at the versions already cached and
        # already known to be clean) plus the one pinned dependency, and is then committed with the fixture.
        seed = os.path.join(fixtures, "node-slugs", "package-lock.json")
        run("docker", "run", "--rm", "-v", f"{fixture}:/app", "-v", f"{seed}:/seed.json:ro", "-v", f"{npm_cache}:/cache", NODE_IMAGE,
            "sh", "-c", "cp -r /app /tmp/work && cd /tmp/work && cp /seed.json package-lock.json && "
                        f"{NPM} install --package-lock-only && cp package-lock.json /app/package-lock.json", env=env)

    # Current version: what `npm ci --offline` restores for the untouched fixture.
    # Target version: its registry metadata (for `npm install --package-lock-only --offline`) and its tarball (for the `npm ci` after it).
    # Last, the worker's exact steps without a network, to prove the cache is enough.
    run("docker", "run", "--rm", "-v", f"{fixture}:/app:ro", "-v", f"{npm_cache}:/cache", NODE_IMAGE,
        "sh", "-c", f"cp -r /app /tmp/work && cd /tmp/work && {NPM} ci && "
                    f"{NPM} install {package}@{target} --save-exact --package-lock-only && {NPM} ci", env=env)
    run("docker", "run", "--rm", "--network", "none", "-v", f"{fixture}:/app:ro", "-v", f"{npm_cache}:/npm-cache:ro", NODE_IMAGE,
        "sh", "-c", "cp -r /app /tmp/work && cp -r /npm-cache /tmp/npm-cache && cd /tmp/work && "
                    f"sed -i 's/\"{package}\": \"{current}\"/\"{package}\": \"{target}\"/' package.json && "
                    "npm install --package-lock-only --offline --ignore-scripts --no-audit --no-fund --cache /tmp/npm-cache && "
                    "npm ci --offline --ignore-scripts --no-audit --no-fund --cache /tmp/npm-cache && npx --no-install jest && "
                    f"grep -q '{package}-{target}.tgz' package-lock.json", env=env)

    name, package, current, target = PY_UPGRADE
    for version in (current, target):  # one pip call per version: two versions of one package cannot be resolved together
        run("docker", "run", "--rm", "-v", f"{wheels}:/w", PY_IMAGE,
            "pip", "download", f"{package}=={version}", "-d", "/w", "-q", "--no-deps", env=env)
    fixture = os.path.join(fixtures, name)
    run("docker", "run", "--rm", "--network", "none", "-v", f"{fixture}:/app:ro", "-v", f"{wheels}:/pip-wheels:ro", PY_IMAGE,
        "sh", "-c", "cp -r /app /tmp/work && cd /tmp/work && "
                    f"sed -i 's/{package}=={current}/{package}=={target}/' requirements.txt && "
                    "python -m pip install --no-index --find-links /pip-wheels -q pytest -r requirements.txt && python -m pytest -q", env=env)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", required=True, help="directory that receives npm-cache/ and wheels/")
    parser.add_argument("--run-tests", action="store_true", help="run the real-container verifier tests afterwards")
    args = parser.parse_args()

    root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    fixtures = os.path.join(root, "scripts", "e2e", "verification-fixtures")
    out = os.path.abspath(args.out)
    npm_cache = os.path.join(out, "npm-cache")
    wheels = os.path.join(out, "wheels")
    os.makedirs(npm_cache, exist_ok=True)
    os.makedirs(wheels, exist_ok=True)

    mounts = {"MSYS_NO_PATHCONV": "1"}
    env = {**os.environ, **mounts}

    # npm: install the fixture from its lockfile into a scratch copy, so the cache holds exactly what `npm ci --offline` will ask for.
    node_fixture = os.path.join(fixtures, "node-slugs")
    run("docker", "run", "--rm", "-v", f"{node_fixture}:/app:ro", "-v", f"{npm_cache}:/cache", NODE_IMAGE,
        "sh", "-c", "cp -r /app /tmp/work && cd /tmp/work && npm ci --cache /cache --ignore-scripts --no-audit --no-fund", env=env)

    # pip: pytest and its dependencies (plus whatever requirements.txt of a project needs, for projects that have any).
    run("docker", "run", "--rm", "-v", f"{wheels}:/w", PY_IMAGE, "pip", "download", "pytest", "-d", "/w", "-q", env=env)

    prepare_upgrade_fixtures(fixtures, npm_cache, wheels, env)

    variables = {
        "CODEXFLOW_TEST_NODE_IMAGE": NODE_IMAGE,
        "CODEXFLOW_TEST_NODE_FIXTURE": os.path.join(fixtures, "node-slugs"),
        "CODEXFLOW_TEST_NODE_CACHE": npm_cache,
        "CODEXFLOW_TEST_PY_IMAGE": PY_IMAGE,
        "CODEXFLOW_TEST_PY_FIXTURE": os.path.join(fixtures, "python-slugs"),
        "CODEXFLOW_TEST_PY_WHEELS": wheels,
    }
    print("\nSet these to run the real-container tests:")
    for name, value in variables.items():
        print(f"  {name}={value}")

    if args.run_tests:
        run("dotnet", "test", os.path.join(root, "CodexFlow.Tests", "CodexFlow.Tests.csproj"), "--filter",
            "FullyQualifiedName~RealContainerVerifierTests", env={**os.environ, **variables})
    return 0


if __name__ == "__main__":
    sys.exit(main())
