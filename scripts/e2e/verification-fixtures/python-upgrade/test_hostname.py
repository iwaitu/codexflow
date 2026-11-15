# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

from hostname import to_ascii


def test_ascii_names_are_kept():
    assert to_ascii("Example.COM") == "example.com"


def test_unicode_names_become_punycode():
    assert to_ascii("bücher.example") == "xn--bcher-kva.example"
