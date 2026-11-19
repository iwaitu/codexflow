#!/usr/bin/env bash
# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
#
# CodexFlow Desktop macOS installer (Apple Silicon / arm64)
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/iwaitu/codexflow/main/scripts/install-macos.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/iwaitu/codexflow/main/scripts/install-macos.sh | bash -s -- --version v0.1.15 --open

set -euo pipefail

REPO="${CODEXFLOW_REPO:-iwaitu/codexflow}"
APP_NAME="CodexFlow Desktop.app"
REQUESTED_VERSION="${CODEXFLOW_VERSION:-}"
INSTALL_DIR=""
LOCAL_DMG=""
AUTO_OPEN=0
SKIP_CHECKSUM=0

usage() {
  cat <<'EOF'
CodexFlow Desktop macOS Installer (Apple Silicon / arm64)

Options:
  --version <vX.Y.Z>  Install a specific release tag (default: latest GitHub release)
  --dir <directory>   Target Applications directory (default: /Applications, fallback: ~/Applications)
  --dmg <file>        Install from a local .dmg file instead of downloading from GitHub
  --open              Launch CodexFlow Desktop automatically after installation
  --skip-checksum     Skip SHA-256 verification (not recommended)
  -h, --help          Show this help message
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --version)
      [ $# -ge 2 ] || { echo "Error: --version requires a value." >&2; exit 1; }
      REQUESTED_VERSION="$2"
      shift 2
      ;;
    --dir)
      [ $# -ge 2 ] || { echo "Error: --dir requires a value." >&2; exit 1; }
      INSTALL_DIR="$2"
      shift 2
      ;;
    --dmg)
      [ $# -ge 2 ] || { echo "Error: --dmg requires a value." >&2; exit 1; }
      LOCAL_DMG="$2"
      shift 2
      ;;
    --open)
      AUTO_OPEN=1
      shift
      ;;
    --skip-checksum)
      SKIP_CHECKSUM=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Error: Unknown option '$1'. Run with --help for usage." >&2
      exit 1
      ;;
  esac
done

if [ "$(uname -s)" != "Darwin" ]; then
  echo "Error: This installer is only for macOS." >&2
  exit 1
fi

ARCH="$(uname -m)"
if [ "$ARCH" != "arm64" ]; then
  echo "Error: CodexFlow Desktop macOS release currently supports Apple Silicon (arm64) only; detected '${ARCH}'." >&2
  exit 1
fi

if [ -z "$INSTALL_DIR" ]; then
  if [ -d "/Applications" ] && [ -w "/Applications" ]; then
    INSTALL_DIR="/Applications"
  else
    INSTALL_DIR="${HOME}/Applications"
    mkdir -p "$INSTALL_DIR"
  fi
else
  mkdir -p "$INSTALL_DIR"
fi

TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/codexflow-install.XXXXXX")"
MOUNT_POINT="${TMP_DIR}/mnt"
BACKUP_APP=""
MOUNTED=0

cleanup() {
  if [ "$MOUNTED" -eq 1 ]; then
    hdiutil detach "$MOUNT_POINT" -quiet 2>/dev/null || hdiutil detach "$MOUNT_POINT" -force -quiet 2>/dev/null || true
  fi
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

DMG_PATH=""
DMG_FILENAME=""
CHECKSUM_URL=""

if [ -n "$LOCAL_DMG" ]; then
  if [ ! -f "$LOCAL_DMG" ]; then
    echo "Error: Local DMG not found: ${LOCAL_DMG}" >&2
    exit 1
  fi
  DMG_PATH="$LOCAL_DMG"
  DMG_FILENAME="$(basename "$LOCAL_DMG")"
  echo "==> Using local installer: ${DMG_PATH}"
else
  if [ -n "$REQUESTED_VERSION" ]; then
    TAG="${REQUESTED_VERSION#v}"
    TAG="v${TAG}"
    API_URL="https://api.github.com/repos/${REPO}/releases/tags/${TAG}"
    echo "==> Fetching release metadata for ${TAG}..."
  else
    API_URL="https://api.github.com/repos/${REPO}/releases/latest"
    echo "==> Fetching latest CodexFlow Desktop release metadata..."
  fi

  RELEASE_JSON="$(curl -fsSL -H "Accept: application/vnd.github+json" "$API_URL")"

  DMG_URL="$(printf '%s\n' "$RELEASE_JSON" | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*mac-arm64\.dmg"' | head -n 1 | sed -E 's/^"browser_download_url"[[:space:]]*:[[:space:]]*"([^"]*)"$/\1/')"
  if [ -z "$DMG_URL" ]; then
    echo "Error: Could not find a mac-arm64.dmg asset in release response from ${API_URL}." >&2
    exit 1
  fi

  CHECKSUM_URL="$(printf '%s\n' "$RELEASE_JSON" | grep -oE '"browser_download_url"[[:space:]]*:[[:space:]]*"[^"]*SHA256SUMS\.txt"' | head -n 1 | sed -E 's/^"browser_download_url"[[:space:]]*:[[:space:]]*"([^"]*)"$/\1/' || true)"

  DMG_FILENAME="$(basename "$DMG_URL")"
  DMG_PATH="${TMP_DIR}/${DMG_FILENAME}"

  echo "==> Downloading ${DMG_FILENAME}..."
  curl -fL --progress-bar "$DMG_URL" -o "$DMG_PATH"

  if [ "$SKIP_CHECKSUM" -eq 0 ] && [ -n "$CHECKSUM_URL" ]; then
    CHECKSUM_FILE="${TMP_DIR}/SHA256SUMS.txt"
    echo "==> Verifying SHA-256 checksum..."
    curl -fsSL "$CHECKSUM_URL" -o "$CHECKSUM_FILE"
    EXPECTED_HASH="$(awk -v name="$DMG_FILENAME" '$2 == name || $2 == ("*" name) { print $1; exit }' "$CHECKSUM_FILE")"
    if [ -n "$EXPECTED_HASH" ]; then
      ACTUAL_HASH="$(shasum -a 256 "$DMG_PATH" | awk '{ print $1 }')"
      if [ "$EXPECTED_HASH" != "$ACTUAL_HASH" ]; then
        echo "Error: SHA-256 checksum mismatch for ${DMG_FILENAME}!" >&2
        echo "  Expected: ${EXPECTED_HASH}" >&2
        echo "  Actual:   ${ACTUAL_HASH}" >&2
        exit 1
      fi
      echo "    SHA-256 verified: ${ACTUAL_HASH}"
    else
      echo "Warning: ${DMG_FILENAME} not listed in SHA256SUMS.txt; continuing." >&2
    fi
  fi
fi

mkdir -p "$MOUNT_POINT"
echo "==> Mounting ${DMG_FILENAME}..."
hdiutil attach "$DMG_PATH" -nobrowse -readonly -quiet -mountpoint "$MOUNT_POINT"
MOUNTED=1

SRC_APP="${MOUNT_POINT}/${APP_NAME}"
if [ ! -d "$SRC_APP" ]; then
  SRC_APP="$(find "$MOUNT_POINT" -maxdepth 2 -name '*.app' -type d | head -n 1 || true)"
fi
if [ -z "$SRC_APP" ] || [ ! -d "$SRC_APP" ]; then
  echo "Error: Could not find '${APP_NAME}' inside mounted DMG." >&2
  exit 1
fi

TARGET_APP="${INSTALL_DIR}/$(basename "$SRC_APP")"
echo "==> Installing to ${TARGET_APP}..."

if [ -d "$TARGET_APP" ]; then
  BACKUP_APP="${TMP_DIR}/backup.app"
  mv "$TARGET_APP" "$BACKUP_APP"
fi

if ! ditto "$SRC_APP" "$TARGET_APP"; then
  echo "Error: Failed to copy application bundle." >&2
  if [ -n "$BACKUP_APP" ] && [ -d "$BACKUP_APP" ]; then
    rm -rf "$TARGET_APP"
    mv "$BACKUP_APP" "$TARGET_APP"
  fi
  exit 1
fi

# Ensure no browser quarantine attribute blocks ad-hoc signed releases.
xattr -dr com.apple.quarantine "$TARGET_APP" 2>/dev/null || true

echo "==> Verifying application bundle signature..."
if codesign --verify --deep --strict "$TARGET_APP" 2>/dev/null; then
  echo "    Signature verified cleanly."
else
  echo "Warning: Bundle signature verification reported a warning (older release without inside-out signing?)." >&2
fi

echo ""
echo "CodexFlow Desktop has been installed to:"
echo "  ${TARGET_APP}"
echo ""
echo "Bundled .NET 10 runtime and LibreOffice are included — no manual .NET installation is required."
echo "You can open CodexFlow Desktop from Spotlight, Applications, or by running:"
echo "  open \"${TARGET_APP}\""

if [ "$AUTO_OPEN" -eq 1 ]; then
  open "$TARGET_APP"
fi
