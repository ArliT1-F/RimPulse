#!/usr/bin/env bash
# Shared helpers for the Linux packaging scripts. Sourced by build-deb.sh / build-appimage.sh; not a
# standalone command. Everything here works on a plain Ubuntu/Debian runner with coreutils and dpkg-deb.

set -euo pipefail

PACKAGING_LINUX_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PACKAGING_DIR="$(dirname -- "$PACKAGING_LINUX_DIR")"
REPO_ROOT="$(dirname -- "$PACKAGING_DIR")"

die() { printf 'error: %s\n' "$*" >&2; exit 1; }
info() { printf '  %s\n' "$*"; }
require_command() { command -v "$1" >/dev/null 2>&1 || die "Required command '$1' is not on PATH."; }

# Debian versions start with a digit and may contain alphanumerics and . + ~ - (not ':' without an epoch).
validate_version() {
    [[ "$1" =~ ^[0-9][0-9A-Za-z.+~-]*$ ]] ||
        die "Invalid package version '$1'. Use e.g. 0.7.0 or 0.7.0~rc1 (tag v0.7.0 → version 0.7.0)."
}

validate_arch() {
    case "$1" in
        amd64 | arm64) ;;
        *) die "Unsupported Linux architecture '$1'. Use amd64 or arm64." ;;
    esac
}

# Sets PUBLISH_DIR, VERSION, ARCH, OUTPUT_DIR, RUNTIME from the shared flags. Scripts parse their own
# extra flags first and pass the remainder here.
parse_common_args() {
    PUBLISH_DIR=""
    VERSION="0.0.0-dev"
    ARCH="amd64"
    OUTPUT_DIR="$REPO_ROOT/artifacts/packages"
    RUNTIME="self-contained"

    while (($#)); do
        case "$1" in
            --publish-dir)
                (($# >= 2)) || die "--publish-dir needs a value"
                PUBLISH_DIR="$2"
                shift 2
                ;;
            --version)
                (($# >= 2)) || die "--version needs a value"
                VERSION="$2"
                shift 2
                ;;
            --arch)
                (($# >= 2)) || die "--arch needs a value"
                ARCH="$2"
                shift 2
                ;;
            --output)
                (($# >= 2)) || die "--output needs a value"
                OUTPUT_DIR="$2"
                shift 2
                ;;
            --runtime)
                (($# >= 2)) || die "--runtime needs a value"
                RUNTIME="$2"
                shift 2
                ;;
            *) die "Unknown argument '$1'. Use --help for usage." ;;
        esac
    done

    [[ -n "$PUBLISH_DIR" ]] || die "--publish-dir is required (the output of dotnet publish)."
    validate_version "$VERSION"
    validate_arch "$ARCH"
    case "$RUNTIME" in
        self-contained | framework-dependent) ;;
        *) die "Invalid --runtime '$RUNTIME'. Use self-contained or framework-dependent." ;;
    esac

    PUBLISH_DIR="$(CDPATH= cd -- "$PUBLISH_DIR" && pwd -P)" || die "Publish directory '$PUBLISH_DIR' does not exist."
    mkdir -p -- "$OUTPUT_DIR"
    OUTPUT_DIR="$(CDPATH= cd -- "$OUTPUT_DIR" && pwd -P)"
}

# A publish directory is only usable when the apphost and the managed entry assembly are present. Copying
# an empty or unrelated directory would produce a package that installs but fails to start.
validate_publish() {
    local publish="$1" label="${2:-publish directory}"
    [[ -d "$publish" ]] || die "$label '$publish' is not a directory."
    [[ -f "$publish/RimPulse" ]] || die "$label is missing the 'RimPulse' apphost; run dotnet publish first."
    [[ -f "$publish/RimPulse.dll" ]] || die "$label is missing 'RimPulse.dll'; run dotnet publish first."
    # Both publish shapes emit at least one manifest; requiring either (not a specific one) keeps the
    # check useful without depending on how a given SDK version lays out self-contained output.
    [[ -f "$publish/RimPulse.deps.json" || -f "$publish/RimPulse.runtimeconfig.json" ]] ||
        die "$label is missing the dependency/runtime manifests; run dotnet publish first."
}

# Copies the published payload into a staging directory and refuses symlinks that escape it. A package
# must be self-contained on the target: an absolute link into the build machine's filesystem is a bug.
stage_payload() {
    local publish="$1" destination="$2"
    validate_publish "$publish"
    mkdir -p -- "$destination"
    cp -a -- "$publish/." "$destination/"
    chmod 755 -- "$destination/RimPulse"

    local link resolved
    while IFS= read -r -d '' link; do
        resolved="$(readlink -f -- "$link" || true)"
        [[ -n "$resolved" && -e "$link" ]] || die "Broken symlink in the publish output: $link"
        case "$resolved" in
            "$destination"/* | "$destination") ;;
            *) die "Symlink '$link' points outside the package payload ('$resolved'); refusing to package it." ;;
        esac
    done < <(find "$destination" -type l -print0)
}

# Desktop entry, hicolor icons and (for .deb) the Debian copyright file.
install_desktop_assets() {
    local stage="$1"
    mkdir -p -- "$stage/usr/share/applications" \
        "$stage/usr/share/icons/hicolor/scalable/apps" \
        "$stage/usr/share/icons/hicolor/256x256/apps"
    cp -- "$PACKAGING_LINUX_DIR/rimpulse.desktop" "$stage/usr/share/applications/rimpulse.desktop"
    cp -- "$PACKAGING_LINUX_DIR/rimpulse.svg" "$stage/usr/share/icons/hicolor/scalable/apps/rimpulse.svg"
    cp -- "$PACKAGING_LINUX_DIR/rimpulse.png" "$stage/usr/share/icons/hicolor/256x256/apps/rimpulse.png"
    chmod 644 -- "$stage/usr/share/applications/rimpulse.desktop" \
        "$stage/usr/share/icons/hicolor/scalable/apps/rimpulse.svg" \
        "$stage/usr/share/icons/hicolor/256x256/apps/rimpulse.png"
}

# Conservative runtime dependencies. Framework-dependent builds additionally need the .NET 10 runtime;
# self-contained builds carry it. The X11/fontconfig/GL libraries are the documented Avalonia Linux
# minimum and are listed here so apt installs them, not so the package claims to have verified them.
debian_depends() {
    case "$RUNTIME" in
        framework-dependent) printf 'dotnet-runtime-10.0, libx11-6, libfontconfig1, libgl1' ;;
        *) printf 'libc6, libx11-6, libfontconfig1, libgl1' ;;
    esac
}

render_control() {
    local destination="$1" version="$2" arch="$3" installed_size="$4" depends="$5"
    sed -e "s|@VERSION@|$version|g" \
        -e "s|@ARCH@|$arch|g" \
        -e "s|@INSTALLED_SIZE@|$installed_size|g" \
        -e "s|@DEPENDS@|$depends|g" \
        "$PACKAGING_LINUX_DIR/control.template" >"$destination"
}

# dpkg-deb uses KiB for Installed-Size; du -sk measures the staged tree including DEBIAN metadata.
installed_size_kb() {
    du -sk -- "$1" | cut -f1
}
