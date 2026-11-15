# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

import idna


def to_ascii(hostname: str) -> str:
    """The ASCII (punycode) form of an internationalized host name, lower-cased."""
    return idna.encode(hostname.strip().lower()).decode("ascii")
