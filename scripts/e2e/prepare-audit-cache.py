#!/usr/bin/env python3
# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Prepare the controlled, read-only NuGet restore cache used by the real-Docker artifact-audit tests.

Builds (inside the pinned verifier image, with network ONLY for this preparation step):
  * Cfaudit.Wrapper 1.0.0  - a trivial local library that depends on Newtonsoft.Json 12.0.3
  * Newtonsoft.Json 12.0.3 - known-vulnerable (GHSA-5crp-9r3c-p9vr, fixed in 13.0.1) => deterministic transitive finding
into a global-packages-layout folder that the isolation backend mounts read-only at /restore-cache.

Configuration comes ONLY from environment variables (no hard-coded paths):
  ARTIFACT_AUDIT_RESTORE_CACHE  (required) output directory, created if missing
  ARTIFACT_AUDIT_IMAGE          (required) verifier image, e.g. codexflow-verification:10.0.403-gitleaks8.30.1
  ARTIFACT_AUDIT_DOCKER         (optional) docker executable, default "docker"
  ARTIFACT_AUDIT_FORCE=1        (optional) rebuild even if the cache is already complete

Exit code 0 only if the cache is complete and verified; any problem exits non-zero (never "skips").
"""
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile

WRAPPER_ID, WRAPPER_VERSION = "cfaudit.wrapper", "1.0.0"
VULN_ID, VULN_VERSION = "newtonsoft.json", "12.0.3"

CONTAINER_SCRIPT = r"""
set -eu
export HOME=/tmp/h DOTNET_CLI_HOME=/tmp/h DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
mkdir -p /tmp/w/Wrapper /tmp/w/consumer /tmp/feed
cd /tmp/w/Wrapper
cat > Wrapper.csproj <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework><PackageId>Cfaudit.Wrapper</PackageId><Version>1.0.0</Version></PropertyGroup>
<ItemGroup><PackageReference Include="Newtonsoft.Json" Version="12.0.3" /></ItemGroup></Project>
XML
echo "namespace W { public class C {} }" > C.cs
dotnet pack -o /tmp/feed -p:NuGetAudit=false
cd /tmp/w/consumer
cat > Consumer.csproj <<'XML'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
<ItemGroup><PackageReference Include="Cfaudit.Wrapper" Version="1.0.0" /></ItemGroup></Project>
XML
echo "System.Console.WriteLine(1);" > P.cs
dotnet restore --source /tmp/feed --source https://api.nuget.org/v3/index.json --packages /cache -p:NuGetAudit=false
chmod -R a+rX /cache
"""


def fail(msg: str) -> "None":
    print(f"prepare-audit-cache: ERROR: {msg}", file=sys.stderr)
    sys.exit(1)


def complete(cache: str) -> bool:
    return all(os.path.isfile(p) for p in (
        os.path.join(cache, WRAPPER_ID, WRAPPER_VERSION, f"{WRAPPER_ID}.{WRAPPER_VERSION}.nupkg"),
        os.path.join(cache, VULN_ID, VULN_VERSION, f"{VULN_ID}.{VULN_VERSION}.nupkg"),
    ))


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> None:
    cache = os.environ.get("ARTIFACT_AUDIT_RESTORE_CACHE", "").strip()
    image = os.environ.get("ARTIFACT_AUDIT_IMAGE", "").strip()
    docker = os.environ.get("ARTIFACT_AUDIT_DOCKER", "docker").strip() or "docker"
    if not cache:
        fail("ARTIFACT_AUDIT_RESTORE_CACHE is not set")
    if not image:
        fail("ARTIFACT_AUDIT_IMAGE is not set")
    cache = os.path.abspath(cache)

    if complete(cache) and os.environ.get("ARTIFACT_AUDIT_FORCE") != "1":
        print(f"prepare-audit-cache: cache already complete at {cache}")
    else:
        if shutil.which(docker) is None:
            fail(f"docker executable '{docker}' not found")
        probe = subprocess.run([docker, "image", "inspect", "--format", "{{.Id}}", image], capture_output=True, text=True)
        if probe.returncode != 0:
            fail(f"image '{image}' is not available locally: {probe.stderr.strip()}")
        parent = os.path.dirname(cache) or "."
        os.makedirs(parent, exist_ok=True)
        staging = tempfile.mkdtemp(prefix=".audit-cache-", dir=parent)
        try:
            cmd = [docker, "run", "--rm", "-v", f"{staging}:/cache", "--entrypoint", "sh"]
            if os.name == "posix":
                cmd += ["--user", f"{os.getuid()}:{os.getgid()}"]
            cmd += [image, "-c", CONTAINER_SCRIPT]
            run = subprocess.run(cmd, capture_output=True, text=True)
            sys.stdout.write(run.stdout[-4000:])
            if run.returncode != 0:
                sys.stderr.write(run.stderr[-4000:])
                fail(f"container build failed with exit code {run.returncode}")
            if not complete(staging):
                fail("cache incomplete after build")
            if os.path.isdir(cache):
                shutil.rmtree(cache)
            os.replace(staging, cache)
            staging = ""
        finally:
            if staging and os.path.isdir(staging):
                shutil.rmtree(staging, ignore_errors=True)

    if not complete(cache):
        fail(f"cache at {cache} is incomplete")
    vuln = os.path.join(cache, VULN_ID, VULN_VERSION, f"{VULN_ID}.{VULN_VERSION}.nupkg")
    print(f"prepare-audit-cache: OK cache={cache}")
    print(f"prepare-audit-cache: {VULN_ID} {VULN_VERSION} nupkg sha256={sha256(vuln)}")


if __name__ == "__main__":
    main()
