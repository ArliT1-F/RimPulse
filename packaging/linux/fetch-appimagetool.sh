#!/usr/bin/env bash
# Download the pinned appimagetool AppImage and verify its SHA-256 before use. This is the same pin and
# the same verification the release workflow uses, so a local AppImage build no longer needs a guessed
# "/path/to/appimagetool-x86_64.AppImage".
#
# Only the resolved absolute path is written to stdout, so callers can use it in a command substitution:
#   tool="$(bash packaging/linux/fetch-appimagetool.sh)"
#   bash packaging/linux/build-appimage.sh --publish-dir ... --appimagetool "$tool" --extract-and-run
set -euo pipefail

HERE="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(CDPATH= cd -- "$HERE/../.." && pwd)"
# shellcheck source=/dev/null
source "$HERE/appimagetool.env"

die() { printf 'error: %s\n' "$*" >&2; exit 1; }
info() { printf '  %s\n' "$*" >&2; }
require_command() { command -v "$1" >/dev/null 2>&1 || die "Required command '$1' is not on PATH."; }

usage() {
    cat <<'EOF'
Usage: fetch-appimagetool.sh [options]

  --output DIR   where to store the tool (default: <repo>/artifacts/tools)
  --arch ARCH    amd64 (default) or arm64
  --force        re-download even if a checksum-valid file is already present
  --check        validate the pin without downloading or writing anything
  -h, --help     show this help

Prints the absolute path of the verified tool on stdout. All other output goes to stderr.
EOF
}

output="$REPO_ROOT/artifacts/tools"
arch="amd64"
force=0
check=0
while (($#)); do
    case "$1" in
        -h | --help)
            usage
            exit 0
            ;;
        --output)
            (($# >= 2)) || die "--output needs a value"
            output="$2"
            shift 2
            ;;
        --arch)
            (($# >= 2)) || die "--arch needs a value"
            arch="$2"
            shift 2
            ;;
        --force)
            force=1
            shift
            ;;
        --check)
            check=1
            shift
            ;;
        *) die "Unknown argument '$1'. Use --help for usage." ;;
    esac
done
case "$arch" in
    amd64)
        suffix="x86_64"
        url="$APPIMAGETOOL_URL_X86_64"
        sha="$APPIMAGETOOL_SHA256_X86_64"
        ;;
    arm64)
        suffix="aarch64"
        url="$APPIMAGETOOL_URL_AARCH64"
        sha="$APPIMAGETOOL_SHA256_AARCH64"
        ;;
    *) die "Unsupported architecture '$arch'. Use amd64 or arm64." ;;
esac

[[ "$sha" =~ ^[0-9a-f]{64}$ ]] || die "The pinned SHA-256 for $suffix is not 64 lowercase hex characters."
[[ "$url" == https://github.com/AppImage/appimagetool/releases/download/"$APPIMAGETOOL_VERSION"/appimagetool-"$suffix".AppImage ]] ||
    die "The pinned URL for $suffix does not match version $APPIMAGETOOL_VERSION."

destination="$output/appimagetool-${suffix}.AppImage"
if ((check)); then
    info "appimagetool $APPIMAGETOOL_VERSION ($suffix) pin is well formed; would download $url"
    exit 0
fi

require_command curl
require_command sha256sum
mkdir -p -- "$output"
output="$(CDPATH= cd -- "$output" && pwd -P)"
destination="$output/appimagetool-${suffix}.AppImage"
verify() { printf '%s  %s\n' "$sha" "$1" | sha256sum -c - >/dev/null 2>&1; }

if ((force == 0)) && [[ -f "$destination" ]] && verify "$destination"; then
    info "Reusing verified $destination"
    printf '%s\n' "$destination"
    exit 0
fi

temporary="$(mktemp "$output/.appimagetool.XXXXXX")"
trap 'rm -f -- "$temporary"' EXIT
info "Downloading appimagetool $APPIMAGETOOL_VERSION ($suffix)…"
curl -fsSL --retry 3 --connect-timeout 20 -o "$temporary" "$url" ||
    die "Could not download '$url'. GitHub serves release assets from release-assets.githubusercontent.com, so both hosts (and any proxy) must be reachable. You can also download the file manually from the $APPIMAGETOOL_VERSION release page, verify its SHA-256 against packaging/linux/appimagetool.env, and pass --appimagetool /real/path."
if ! verify "$temporary"; then
    die "SHA-256 mismatch for the downloaded appimagetool. Do not use it; re-check the pin in packaging/linux/appimagetool.env."
fi
chmod 755 -- "$temporary"
mv -f -- "$temporary" "$destination"
trap - EXIT
info "Verified and wrote $destination"
printf '%s\n' "$destination"
