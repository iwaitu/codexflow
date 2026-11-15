#!/usr/bin/env python3
# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Prepares the OSV advisory data the host reads for npm and Python dependency scans.

The dependency gate never goes to the network while it scans; it reads a host directory
(ArtifactAudit:OsvAdvisoryDirectory) laid out as

    <dir>/npm/snapshot.json   + advisories.ndjson
    <dir>/PyPI/snapshot.json  + advisories.ndjson

This script downloads the per-ecosystem all.zip of osv.dev (npm is a few hundred MB, PyPI tens of MB) and reduces it to ONE
file per ecosystem, advisories.ndjson: one line per advisory holding only what the gate needs (id, severity, affected packages with
their ranges and versions). Descriptions and references are dropped on purpose:

  * the unpacked all.zip is hundreds of thousands of small files; reading them is slow, and on Windows the antivirus scans every one
    of them (and quarantines the records of malicious packages, whose text contains the malicious code), so records silently go
    missing. One file of ids and version ranges has nothing to flag and loads in seconds;
  * snapshot.json records how many advisories the file holds and its SHA-256, so a file that lost lines or was changed is
    rejected by the host instead of being used with holes in it.

Run it again to refresh: the gate treats data older than ArtifactAudit:Advisory:MaxAge (48 hours by default) as unusable.

    python scripts/e2e/prepare-osv-advisories.py --out ./osv-advisories
    python scripts/e2e/prepare-osv-advisories.py --out ./osv-advisories --zip-dir ./downloads --ecosystems PyPI
"""
import argparse
import email.utils
import hashlib
import json
import os
import shutil
import sys
import urllib.request
import zipfile
from datetime import datetime, timezone

BASE = "https://osv-vulnerabilities.storage.googleapis.com"
INDEX = "advisories.ndjson"


def download(ecosystem: str, target: str) -> datetime:
    """Downloads <ecosystem>/all.zip to target and returns the Last-Modified time of the source."""
    url = f"{BASE}/{ecosystem}/all.zip"
    print(f"downloading {url}", flush=True)
    with urllib.request.urlopen(url) as response, open(target, "wb") as out:
        modified = response.headers.get("Last-Modified")
        shutil.copyfileobj(response, out)
    return email.utils.parsedate_to_datetime(modified) if modified else datetime.now(timezone.utc)


def reduce_record(record: dict, ecosystem: str):
    """Keeps id, severity and the affected entries of this ecosystem; None for withdrawn records or records of other ecosystems."""
    if isinstance(record.get("withdrawn"), str) or not isinstance(record.get("id"), str):
        return None
    affected = []
    for entry in record.get("affected") or []:
        package = entry.get("package") or {}
        if str(package.get("ecosystem", "")).lower() != ecosystem.lower() or not package.get("name"):
            continue
        affected.append({
            "package": {"ecosystem": ecosystem, "name": package["name"]},
            "ranges": [{"type": r.get("type", ""), "events": r.get("events") or []} for r in entry.get("ranges") or []],
            "versions": [v for v in entry.get("versions") or [] if isinstance(v, str)],
        })
    if not affected:
        return None
    reduced = {"id": record["id"], "affected": affected}
    severity = (record.get("database_specific") or {}).get("severity")
    if isinstance(severity, str):
        reduced["database_specific"] = {"severity": severity}
    return reduced


def build_index(archive: str, folder: str, ecosystem: str):
    """Writes advisories.ndjson from the archive without unpacking it; returns (advisory count, sha256 of the file)."""
    os.makedirs(folder, exist_ok=True)
    for name in os.listdir(folder):  # records unpacked by an earlier version of this script
        if name.endswith(".json") and name != "snapshot.json":
            os.remove(os.path.join(folder, name))
    path = os.path.join(folder, INDEX)
    digest = hashlib.sha256()
    count = 0
    with zipfile.ZipFile(archive) as zf, open(path, "wb") as out:
        for member in zf.namelist():
            if member.endswith("/") or "/" in member or not member.endswith(".json"):
                continue
            with zf.open(member) as src:
                reduced = reduce_record(json.load(src), ecosystem)
            if reduced is None:
                continue
            line = (json.dumps(reduced, separators=(",", ":"), ensure_ascii=True) + "\n").encode("ascii")
            out.write(line)
            digest.update(line)
            count += 1
    return count, digest.hexdigest()


def stamps(archive: str, downloaded_source_updated, now=None):
    """(retrieved at, source updated at) for the snapshot built from ``archive``.

    A freshly downloaded archive was retrieved now, and its source was updated when the server says (``downloaded_source_updated``). An archive
    that was REUSED (``downloaded_source_updated`` is None) was retrieved when it was downloaded, which is when the file was written: stamping it
    "retrieved now" makes days-old advisories look fresh to a gate that refuses data older than 48 hours.
    """
    if downloaded_source_updated is not None:
        return (now or datetime.now(timezone.utc)), downloaded_source_updated.astimezone(timezone.utc)
    written = datetime.fromtimestamp(os.path.getmtime(archive), timezone.utc)
    return written, written


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", required=True, help="the advisory directory (ArtifactAudit:OsvAdvisoryDirectory)")
    parser.add_argument("--zip-dir", help="where the all.zip files are kept; an existing <ecosystem>.zip there is reused, not downloaded")
    parser.add_argument("--ecosystems", nargs="+", default=["npm", "PyPI"], choices=["npm", "PyPI"])
    args = parser.parse_args()

    out = os.path.abspath(args.out)
    zip_dir = os.path.abspath(args.zip_dir) if args.zip_dir else os.path.join(out, ".downloads")
    os.makedirs(zip_dir, exist_ok=True)

    for ecosystem in args.ecosystems:
        archive = os.path.join(zip_dir, f"{ecosystem}.zip")
        if os.path.exists(archive):
            print(f"reusing {archive} (its data is as old as the file)", flush=True)
            downloaded = None
        else:
            downloaded = download(ecosystem, archive)
        retrieved_at, source_updated = stamps(archive, downloaded)
        folder = os.path.join(out, ecosystem)
        count, sha256 = build_index(archive, folder, ecosystem)
        with open(os.path.join(folder, "snapshot.json"), "w", encoding="utf-8") as f:
            json.dump({"retrievedAtUtc": retrieved_at.isoformat(),
                       "sourceUpdatedUtc": source_updated.isoformat(),
                       "records": count, "indexSha256": sha256}, f)
        print(f"{ecosystem}: {count} advisories in {os.path.join(folder, INDEX)}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
