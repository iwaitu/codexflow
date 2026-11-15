# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""The reviewed cache builder generates one restore project per group of exact package pins."""
import importlib.util
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "prepare-restore-cache.py"
spec = importlib.util.spec_from_file_location("prepare_restore_cache", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class GeneratedProjectTests(unittest.TestCase):
    def test_every_root_is_pinned_to_one_exact_version(self):
        text = module.generate_csproj("net10.0", [("MessagePack", "2.5.303"), ("Snappier", "1.3.1")])
        self.assertIn('Include="MessagePack" Version="[2.5.303]"', text)
        self.assertIn('Include="Snappier" Version="[1.3.1]"', text)

    def test_a_package_downgrade_between_pins_does_not_stop_the_cache_from_being_built(self):
        # Seen live: Aspire.Hosting.AppHost 13.6.5 asks for MessagePack >= 2.5.307 while the host pinned MessagePack to 2.5.303 as the smallest
        # fix of its own advisories. NU1605 is promoted to an error by the SDK, which made the whole overlay (and the upgrade) fail.
        text = module.generate_csproj("net10.0", [("Aspire.Hosting.AppHost", "13.6.5"), ("MessagePack", "2.5.303")])
        self.assertIn("<NoWarn>$(NoWarn);NU1605</NoWarn>", text)

    def test_the_audit_stays_off_in_the_generated_project(self):
        self.assertIn("<NuGetAudit>false</NuGetAudit>", module.generate_csproj("net10.0", [("A", "1.0.0")]))


if __name__ == "__main__":
    unittest.main()
