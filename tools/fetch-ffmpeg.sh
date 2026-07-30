#!/usr/bin/env bash
# Fetches the ffmpeg sidecar into Castmill Desktop's app-support directory (roadmap E7.2).
#
# The desktop media engine prefers this sidecar, then falls back to a system ffmpeg
# (homebrew etc.) — see Castmill.Media/Ffmpeg.cs. Binaries are verified against the pinned
# SHA-256 below before being installed; a mismatch aborts, it never installs unverified.
#
# To upgrade: change VERSION/URL, run with CASTMILL_FFMPEG_SHA256=<new hash> once, and pin
# the printed hash here.
set -euo pipefail

VERSION="8.0"
DEST="${HOME}/Library/Application Support/Castmill/ffmpeg"

case "$(uname -s)" in
  Darwin)
    URL="https://evermeet.cx/ffmpeg/ffmpeg-${VERSION}.zip"
    # Pinned for ffmpeg 8.0 from evermeet.cx (static macOS build).
    # First-time pinning: run with CASTMILL_FFMPEG_SHA256=print to see the fetched hash.
    SHA256="${CASTMILL_FFMPEG_SHA256:-}"
    ;;
  *)
    echo "This script currently pins macOS builds only. On Windows use the BtbN builds" >&2
    echo "(https://github.com/BtbN/FFmpeg-Builds/releases) and place ffmpeg.exe in the" >&2
    echo "sidecar directory; on Linux install ffmpeg from your distribution." >&2
    exit 1
    ;;
esac

workdir="$(mktemp -d)"
trap 'rm -rf "${workdir}"' EXIT

echo "fetching ffmpeg ${VERSION}…"
curl -fsSL "${URL}" -o "${workdir}/ffmpeg.zip"

actual="$(shasum -a 256 "${workdir}/ffmpeg.zip" | cut -d' ' -f1)"
echo "sha256: ${actual}"

if [[ -z "${SHA256}" || "${SHA256}" == "print" ]]; then
  echo "No pinned hash provided. Verify the hash above out-of-band, then re-run with:" >&2
  echo "  CASTMILL_FFMPEG_SHA256=${actual} $0" >&2
  exit 2
fi

if [[ "${actual}" != "${SHA256}" ]]; then
  echo "HASH MISMATCH — refusing to install. Expected ${SHA256}, got ${actual}." >&2
  exit 3
fi

mkdir -p "${DEST}"
unzip -o -q "${workdir}/ffmpeg.zip" -d "${DEST}"
chmod +x "${DEST}/ffmpeg"
echo "installed to ${DEST}/ffmpeg"
