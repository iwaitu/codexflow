# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""The OSV advisory data the dependency gate reads carries two times: when it was retrieved, and when its source was last updated."""
import importlib.util
import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "prepare-osv-advisories.py"
spec = importlib.util.spec_from_file_location("prepare_osv_advisories", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

NOW = datetime(2026, 11, 15, 12, 0, tzinfo=timezone.utc)


class StampTests(unittest.TestCase):
    """The gate refuses data retrieved more than 48 hours ago. Re-running the script on an archive that was downloaded days ago used to stamp it
    'retrieved now', so two-day-old advisories looked fresh (found while refreshing the data for the Python scenario, 2026-11-15)."""

    def test_a_reused_archive_was_retrieved_when_it_was_downloaded_not_now(self):
        with tempfile.TemporaryDirectory() as directory:
            archive = os.path.join(directory, "PyPI.zip")
            Path(archive).write_bytes(b"zip")
            downloaded = NOW - timedelta(days=3)
            os.utime(archive, (downloaded.timestamp(), downloaded.timestamp()))

            retrieved, source_updated = module.stamps(archive, None, NOW)

            self.assertEqual(downloaded, retrieved)
            self.assertEqual(downloaded, source_updated)

    def test_a_downloaded_archive_was_retrieved_now_and_its_source_is_what_the_server_said(self):
        source = NOW - timedelta(hours=2)

        retrieved, source_updated = module.stamps("unused.zip", source, NOW)

        self.assertEqual(NOW, retrieved)
        self.assertEqual(source, source_updated)


if __name__ == "__main__":
    unittest.main()
