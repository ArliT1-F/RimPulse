#!/usr/bin/env bash
# Build an AppImage from a self-contained `dotnet publish` directory. appimagetool is downloaded and
# checksum-verified by the release workflow; locally, pass --appimagetool or use --appdir-only to inspect
# the staged AppDir without creating a runnable image. See packaging/README.md.
set -euo pipefail
source "$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<'EOF'
Usage: build-appimage.sh --publish-dir DIR [options]

  --publish-dir DIR    output of `dotnet publish src/RimPulse.App -c Release -r linux-x64 --self-contained true`
  --version VERSION    package/app version (default: 0.0.0-dev)
  --arch ARCH          amd64 or arm64 (default: amd64)
  --runtime KIND       self-contained (default) or framework-dependent
  --output DIR         destination directory (default: <repo>/artifacts/packages)
  --appimagetool PATH  path to a checksum-verified appimagetool AppImage
  --appdir-only        keep the staged AppDir instead of building an AppImage
  --extract-and-run    invoke appimagetool with --appimage-extract-and-run (no FUSE needed)

Produces RimPulse-<version>-<arch>.AppImage, or <output>/RimPulse-<version>-<arch>.AppDir with
--appdir-only. Environment variables APPIMAGETOOL and APPIMAGETOOL_EXTRACT_AND_RUN are also honoured.
EOF
}

appimagetool="${APPIMAGETOOL:-}"
appdir_only=0
extract_and_run=${APPIMAGETOOL_EXTRACT_AND_RUN:-0}
own_args=()
for arg in "$@"; do
    if [[ "$arg" == "-h" || "$arg" == "--help" ]]; then
        usage
        exit 0
    fi
done
while (($#)); do
    case "$1" in
        --appimagetool)
            (($# >= 2)) || die "--appimagetool needs a value"
            appimagetool="$2"
            shift 2
            ;;
        --appdir-only)
            appdir_only=1
            shift
            ;;
        --extract-and-run)
            extract_and_run=1
            shift
            ;;
        *)
            own_args+=("$1")
            shift
            ;;
    esac
done
parse_common_args "${own_args[@]}"

info "Publish directory: $PUBLISH_DIR"
info "AppDir version: $VERSION ($ARCH, $RUNTIME)"

appdir_name="RimPulse-${VERSION}-${ARCH}.AppDir"
workdir="$(mktemp -d "${TMPDIR:-/tmp}/rimpulse-appimage.XXXXXX")"
appdir="$workdir/$appdir_name"
trap 'rm -rf -- "$workdir"' EXIT

stage_payload "$PUBLISH_DIR" "$appdir/usr/lib/rimpulse"
install_desktop_assets "$appdir"

mkdir -p -- "$appdir/usr/bin"
cp -- "$PACKAGING_LINUX_DIR/launcher-appimage.sh" "$appdir/usr/bin/rimpulse"
cp -- "$PACKAGING_LINUX_DIR/AppRun" "$appdir/AppRun"
cp -- "$PACKAGING_LINUX_DIR/rimpulse.desktop" "$appdir/rimpulse.desktop"
cp -- "$PACKAGING_LINUX_DIR/rimpulse.svg" "$appdir/rimpulse.svg"
cp -- "$PACKAGING_LINUX_DIR/rimpulse.png" "$appdir/rimpulse.png"
cp -- "$PACKAGING_LINUX_DIR/rimpulse.png" "$appdir/.DirIcon"
chmod 755 -- "$appdir/usr/bin/rimpulse" "$appdir/AppRun"
chmod 644 -- "$appdir/rimpulse.desktop" "$appdir/rimpulse.svg" "$appdir/rimpulse.png" "$appdir/.DirIcon"

# AppImage metadata checks that otherwise only appear at run time on a desktop.
[[ -x "$appdir/AppRun" ]] || die "AppRun is not executable."
grep -q '^Exec=rimpulse$' "$appdir/rimpulse.desktop" || die "AppDir desktop entry has no Exec=rimpulse."
grep -q '^Icon=rimpulse$' "$appdir/rimpulse.desktop" || die "AppDir desktop entry has no Icon=rimpulse."

if ((appdir_only)); then
    destination="$OUTPUT_DIR/$appdir_name"
    rm -rf -- "$destination"
    cp -a -- "$appdir" "$destination"
    info "Wrote $destination (AppDir only; no AppImage was built)."
    exit 0
fi

if [[ -z "$appimagetool" ]] && command -v appimagetool >/dev/null 2>&1; then
    appimagetool="$(command -v appimagetool)"
fi
[[ -n "$appimagetool" ]] || die "No appimagetool. Pass --appimagetool PATH, set APPIMAGETOOL, or use --appdir-only."
[[ -x "$appimagetool" ]] || die "appimagetool '$appimagetool' is not executable."

case "$ARCH" in
    amd64) appimage_arch="x86_64" ;;
    arm64) appimage_arch="aarch64" ;;
esac
image="$OUTPUT_DIR/RimPulse-${VERSION}-${appimage_arch}.AppImage"
rm -f -- "$image"

tool_args=()
if [[ "$extract_and_run" != "0" ]]; then
    tool_args+=(--appimage-extract-and-run)
fi
tool_args+=(--no-appstream)
info "Running: ARCH=$appimage_arch appimagetool ${tool_args[*]} <AppDir> <image>"
ARCH="$appimage_arch" "$appimagetool" "${tool_args[@]}" "$appdir" "$image"

[[ -s "$image" ]] || die "appimagetool finished without producing '$image'."
chmod 755 -- "$image"
info "Wrote $image"
info "Run it with: ./$image (or: APPIMAGE_EXTRACT_AND_RUN=1 ./$image on systems without FUSE)"
