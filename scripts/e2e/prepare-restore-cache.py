#!/usr/bin/env python3
# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Build a TRUSTED, reviewed NuGet restore cache for the network-less verification guest.

This is the script operators use to create the cache mounted read-only at /restore-cache (Isolation:RestoreCacheHostPath).
It is NOT scripts/e2e/prepare-audit-cache.py, which builds a deliberately vulnerable attack fixture for tests.

Trust model
  * Input is ONLY an explicitly reviewed JSON list of exact package ids/versions (schema below). No target repository,
    project file, props/targets, lock file or nuget.config is read, copied, parsed or executed.
  * The script generates its own minimal project and nuget.config INSIDE the pinned verifier image, restores into a brand
    new cache directory and only from https://api.nuget.org/v3/index.json (all other sources cleared). The prep step has
    network access; actual verification runs keep `--network none`.
  * Nothing from the host leaks in: no host NuGet.config, credentials, HOME or global-packages folder is mounted or copied.
    The only mount is the new, empty output directory. The container uses HOME=/tmp/h.
  * After restore the host re-verifies the cache: every requested root exists at the exact version, every package came from
    nuget.org (.nupkg.metadata "source"), and a manifest with the SHA-256 of every .nupkg is written. Any missing package,
    unexpected source, restore error or non-empty output directory => exit code != 0 (never a silent partial cache).

Environment (the only configuration; nothing is hard-coded)
  ARTIFACT_AUDIT_CACHE_OUT       (required) NEW or EMPTY directory that becomes the cache
  ARTIFACT_AUDIT_CACHE_PACKAGES  (required) path of the reviewed packages JSON
  ARTIFACT_AUDIT_IMAGE           (required) verifier image (must already exist locally; never pulled)
  ARTIFACT_AUDIT_DOCKER          (optional) docker executable, default "docker"
  ARTIFACT_AUDIT_CACHE_VERIFY_ONLY=1  re-verify an existing cache against its restore-cache-manifest.json (no network, no docker)

Reviewed packages JSON (exact versions only; ranges, wildcards and floating versions are rejected)
  {
    "schema": 1,
    "reviewedBy": "<person or process that reviewed the list>",
    "reviewedAtUtc": "2026-11-08T00:00:00Z",
    "targetFramework": "net10.0",              (optional, default net10.0)
    "packages": [ {"id": "Newtonsoft.Json", "version": "13.0.3"} ]
  }
Transitive dependencies are restored with their roots and are listed in the manifest with hashes; review the manifest.

Several graphs (repositories with many projects)
  NuGet resolves transitive versions PER PROJECT, so one merged project can pick different transitive versions than the real
  projects need (the guest then fails offline: honest, but useless). Instead of "packages" you may give "groups": each group
  is restored as its own generated project, all into the same cache:
    "groups": [ {"name": "App", "packages": [ {"id": "...", "version": "..."} ]}, {"name": "Tests", "packages": [ ... ]} ]
  A group should list the direct package references of one real project PLUS those of its ProjectReference closure.
  A group may also carry "sdkPackages": [ {"id": "Aspire.AppHost.Sdk", "version": "9.5.0"} ] for MSBuild SDK packages that a
  project pulls with <Sdk Name=... Version=.../> (they cannot be PackageReferences: NU1213). They are fetched with an exact
  <PackageDownload> only; the SDK itself is NOT evaluated or executed here. NuGet-resolved SDKs also add IMPLICIT package
  references whose ids/versions the SDK decides (Aspire AppHost: Aspire.Dashboard.Sdk.linux-x64 and
  Aspire.Hosting.Orchestration.linux-x64 at the Aspire.Hosting.AppHost version): list those under "sdkPackages" too.
  The audit service seeds SDK packages from /restore-cache into the guest's global packages folder before restore
  (the NuGet SDK resolver does not look into fallback folders).
  "packages" and "groups" are mutually exclusive; ids must be unique inside a group (not across groups).

Output: <cache>/restore-cache-manifest.json (schema, image id, source, input hash, roots, every package with nupkg SHA-256,
manifest SHA-256). Exit 0 only when the cache is complete and verified.
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone

OFFICIAL_SOURCE = "https://api.nuget.org/v3/index.json"
MANIFEST_NAME = "restore-cache-manifest.json"
ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+(\.\d+)?(-[0-9A-Za-z][0-9A-Za-z.-]*)?$")
TFM_RE = re.compile(r"^net\d+\.\d+$")
MAX_PACKAGES = 500
ALLOWED_KEYS = {"schema", "reviewedBy", "reviewedAtUtc", "targetFramework", "packages", "groups"}
GROUP_NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
SPLIT = "\n#####SPLIT#####\n"

CONTAINER_SCRIPT = r"""
set -eu
export HOME=/tmp/h DOTNET_CLI_HOME=/tmp/h DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
unset NUGET_PACKAGES NUGET_HTTP_CACHE_PATH
mkdir -p /tmp/w && cd /tmp/w
cat > all.txt
awk 'BEGIN{n=0; f="p0.csproj"} /^#####SPLIT#####[[:space:]]*$/ {n++; f="p" n ".csproj"; next} {print > f}' all.txt
cat > nuget.config <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
XML
echo 'System.Console.WriteLine(1);' > p.cs
for proj in p*.csproj; do
  dotnet restore "$proj" --configfile /tmp/w/nuget.config --source https://api.nuget.org/v3/index.json --packages /cache \
    -p:NuGetAudit=false -p:RestoreAdditionalProjectSources= -p:RestoreFallbackFolders= --nologo --verbosity minimal
done
chmod -R a+rX /cache
"""


def fail(msg: str) -> None:
    print(f"prepare-restore-cache: ERROR: {msg}", file=sys.stderr)
    sys.exit(1)


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def validate_packages(pkgs) -> list:
    if not isinstance(pkgs, list) or not pkgs:
        fail("'packages' must be a non-empty list")
    if len(pkgs) > MAX_PACKAGES:
        fail(f"too many packages (max {MAX_PACKAGES})")
    seen = set()
    roots = []
    for item in pkgs:
        if not isinstance(item, dict) or set(item) != {"id", "version"}:
            fail("each package must be exactly {\"id\": ..., \"version\": ...}")
        pid, ver = item["id"], item["version"]
        if not isinstance(pid, str) or not ID_RE.match(pid):
            fail(f"invalid package id: {pid!r}")
        if not isinstance(ver, str) or not VERSION_RE.match(ver):
            fail(f"version for {pid} must be an exact version (no ranges/wildcards/floating): {ver!r}")
        if pid.lower() in seen:
            fail(f"duplicate package id: {pid}")
        seen.add(pid.lower())
        roots.append((pid, ver))
    return roots


def load_reviewed(path: str) -> dict:
    try:
        with open(path, "rb") as f:
            raw = f.read()
        data = json.loads(raw.decode("utf-8"))
    except (OSError, ValueError) as ex:
        fail(f"cannot read reviewed packages JSON '{path}': {ex}")
    if not isinstance(data, dict):
        fail("packages JSON must be an object")
    unknown = set(data) - ALLOWED_KEYS
    if unknown:
        fail(f"unknown keys in packages JSON: {sorted(unknown)}")
    if data.get("schema") != 1:
        fail("schema must be 1")
    for key in ("reviewedBy", "reviewedAtUtc"):
        if not isinstance(data.get(key), str) or not data[key].strip():
            fail(f"'{key}' is required: the list must be explicitly reviewed")
    try:
        datetime.fromisoformat(data["reviewedAtUtc"].replace("Z", "+00:00"))
    except ValueError:
        fail("reviewedAtUtc must be an ISO-8601 timestamp")
    tfm = data.get("targetFramework", "net10.0")
    if not isinstance(tfm, str) or not TFM_RE.match(tfm):
        fail("targetFramework must look like net10.0")
    has_pk, has_gr = "packages" in data, "groups" in data
    if has_pk == has_gr:
        fail("give exactly one of 'packages' or 'groups'")
    groups = []
    if has_pk:
        groups.append(("default", validate_packages(data["packages"]), []))
    else:
        raw_groups = data["groups"]
        if not isinstance(raw_groups, list) or not raw_groups or len(raw_groups) > 64:
            fail("'groups' must be a non-empty list (max 64)")
        names = set()
        for g in raw_groups:
            if not isinstance(g, dict) or not ({"name", "packages"} <= set(g) <= {"name", "packages", "sdkPackages"}):
                fail("each group must be {\"name\": ..., \"packages\": [...]} with optional \"sdkPackages\"")
            if not isinstance(g["name"], str) or not GROUP_NAME_RE.match(g["name"]) or g["name"].lower() in names:
                fail(f"invalid or duplicate group name: {g.get('name')!r}")
            names.add(g["name"].lower())
            sdks = validate_packages(g["sdkPackages"]) if "sdkPackages" in g else []
            groups.append((g["name"], validate_packages(g["packages"]), sdks))
    roots = sorted({(i.lower(), v.lower()): (i, v) for _, g, sd in groups for i, v in list(g) + list(sd)}.values(), key=lambda x: (x[0].lower(), x[1]))
    return {"raw_sha256": hashlib.sha256(raw).hexdigest(), "reviewedBy": data["reviewedBy"].strip(),
            "reviewedAtUtc": data["reviewedAtUtc"], "targetFramework": tfm, "roots": roots, "groups": groups}


def generate_csproj(tfm: str, roots, sdks=()) -> str:
    items = "\n".join(f'    <PackageReference Include="{pid}" Version="[{ver}]" />' for pid, ver in roots)
    if sdks:
        items += "\n" + "\n".join(f'    <PackageDownload Include="{pid}" Version="[{ver}]" />' for pid, ver in sdks)
    # NU1605 (package downgrade) is silenced here: the SDK promotes it to an error. Every root is pinned to one exact version chosen by the host, each
    # independently as the smallest fix of its own advisories, so a root may sit below what another root's dependencies ask for
    # (seen: Aspire.Hosting.AppHost 13.6.5 wants MessagePack >= 2.5.307 while MessagePack was pinned to 2.5.303). The cache only has to
    # HOLD every requested root at its exact version; whether a real project's mix of versions restores is decided later, offline,
    # by the verification itself, which is not relaxed.
    return ('<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n'
            f'    <TargetFramework>{tfm}</TargetFramework>\n    <NuGetAudit>false</NuGetAudit>\n'
            '    <NoWarn>$(NoWarn);NU1605</NoWarn>\n  </PropertyGroup>\n'
            f'  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n')


def scan_cache(cache: str):
    """Returns list of {id, version, nupkgSha256, source}; fails on anything that is not an expected nuget.org package."""
    packages = []
    for pid in sorted(os.listdir(cache)):
        pdir = os.path.join(cache, pid)
        if not os.path.isdir(pdir):
            continue  # manifest file etc.
        for ver in sorted(os.listdir(pdir)):
            vdir = os.path.join(pdir, ver)
            if not os.path.isdir(vdir):
                fail(f"unexpected file in cache: {pid}/{ver}")
            nupkg = os.path.join(vdir, f"{pid}.{ver}.nupkg")
            meta = os.path.join(vdir, ".nupkg.metadata")
            if not os.path.isfile(nupkg):
                fail(f"incomplete package folder (no nupkg): {pid}/{ver}")
            source = ""
            if os.path.isfile(meta):
                try:
                    with open(meta, "rb") as f:
                        source = str(json.load(f).get("source", ""))
                except (OSError, ValueError):
                    fail(f"unreadable .nupkg.metadata: {pid}/{ver}")
            if source.rstrip("/") != OFFICIAL_SOURCE:
                fail(f"package {pid} {ver} was not restored from nuget.org (source={source!r})")
            packages.append({"id": pid, "version": ver, "nupkgSha256": sha256_file(nupkg), "source": source})
    if not packages:
        fail("cache contains no packages")
    return packages


def packages_digest(packages) -> str:
    canon = "\n".join(f"{p['id'].lower()}|{p['version'].lower()}|{p['nupkgSha256']}" for p in sorted(packages, key=lambda x: (x["id"].lower(), x["version"].lower())))
    return hashlib.sha256(canon.encode("utf-8")).hexdigest()


def verify_only(cache: str) -> None:
    mpath = os.path.join(cache, MANIFEST_NAME)
    if not os.path.isfile(mpath):
        fail(f"{mpath} not found")
    try:
        with open(mpath, "rb") as f:
            manifest = json.load(f)
    except (OSError, ValueError) as ex:
        fail(f"unreadable manifest: {ex}")
    actual = scan_cache(cache)
    if packages_digest(actual) != manifest.get("packagesSha256"):
        fail("cache content differs from its manifest (package added/removed/modified)")
    have = {(p["id"].lower(), p["version"].lower()) for p in actual}
    for r in manifest.get("roots", []):
        if (r["id"].lower(), r["version"].lower()) not in have:
            fail(f"root {r['id']} {r['version']} missing")
    print(f"prepare-restore-cache: VERIFIED {len(actual)} packages, packagesSha256={manifest['packagesSha256']}")


def main() -> None:
    out = os.environ.get("ARTIFACT_AUDIT_CACHE_OUT", "").strip()
    if not out:
        fail("ARTIFACT_AUDIT_CACHE_OUT is not set")
    out = os.path.abspath(out)
    if os.environ.get("ARTIFACT_AUDIT_CACHE_VERIFY_ONLY") == "1":
        verify_only(out)
        return

    list_path = os.environ.get("ARTIFACT_AUDIT_CACHE_PACKAGES", "").strip()
    image = os.environ.get("ARTIFACT_AUDIT_IMAGE", "").strip()
    docker = os.environ.get("ARTIFACT_AUDIT_DOCKER", "docker").strip() or "docker"
    if not list_path:
        fail("ARTIFACT_AUDIT_CACHE_PACKAGES is not set")
    if not image:
        fail("ARTIFACT_AUDIT_IMAGE is not set")
    if os.path.exists(out) and (not os.path.isdir(out) or os.listdir(out)):
        fail(f"output '{out}' exists and is not an empty directory; a new cache is always built from scratch")
    reviewed = load_reviewed(list_path)
    if shutil.which(docker) is None:
        fail(f"docker executable '{docker}' not found")
    probe = subprocess.run([docker, "image", "inspect", "--format", "{{.Id}}", image], capture_output=True, text=True)
    if probe.returncode != 0:
        fail(f"image '{image}' is not available locally (it is never pulled): {probe.stderr.strip()}")
    image_id = probe.stdout.strip()

    parent = os.path.dirname(out) or "."
    os.makedirs(parent, exist_ok=True)
    staging = tempfile.mkdtemp(prefix=".restore-cache-", dir=parent)
    try:
        cmd = [docker, "run", "--rm", "-i", "-v", f"{staging}:/cache", "--entrypoint", "sh"]
        if os.name == "posix":
            cmd += ["--user", f"{os.getuid()}:{os.getgid()}"]
        cmd += [image, "-c", CONTAINER_SCRIPT]
        run = subprocess.run(cmd, input=SPLIT.join(generate_csproj(reviewed["targetFramework"], g, sd) for _, g, sd in reviewed["groups"]), capture_output=True, text=True)
        if run.returncode != 0:
            sys.stderr.write((run.stdout + run.stderr)[-4000:])
            fail(f"restore failed in the verifier image (exit {run.returncode})")

        packages = scan_cache(staging)
        have = {(p["id"].lower(), p["version"].lower()) for p in packages}
        for pid, ver in reviewed["roots"]:
            if (pid.lower(), ver.lower()) not in have:
                fail(f"requested package {pid} {ver} is missing from the cache")
        manifest = {
            "schema": 1,
            "createdAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "image": image, "imageId": image_id, "source": OFFICIAL_SOURCE,
            "reviewedBy": reviewed["reviewedBy"], "reviewedAtUtc": reviewed["reviewedAtUtc"],
            "inputListSha256": reviewed["raw_sha256"], "targetFramework": reviewed["targetFramework"],
            "roots": [{"id": i, "version": v} for i, v in reviewed["roots"]],
            "groups": [{"name": n, "packages": [{"id": i, "version": v} for i, v in g], "sdkPackages": [{"id": i, "version": v} for i, v in sd]} for n, g, sd in reviewed["groups"]],
            "packages": packages, "packagesSha256": packages_digest(packages),
        }
        with open(os.path.join(staging, MANIFEST_NAME), "w", encoding="utf-8") as f:
            json.dump(manifest, f, indent=2)
        if os.path.isdir(out):
            os.rmdir(out)  # empty by the check above
        os.replace(staging, out)
        staging = ""
    finally:
        if staging and os.path.isdir(staging):
            shutil.rmtree(staging, ignore_errors=True)

    verify_only(out)
    print(f"prepare-restore-cache: OK cache={out} roots={len(reviewed['roots'])} imageId={image_id}")


if __name__ == "__main__":
    main()
