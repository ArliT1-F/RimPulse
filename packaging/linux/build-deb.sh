#!/usr/bin/env bash
# Build a .deb from a `dotnet publish` directory. No network access, no root and no SDK required here:
# the heavy work (publishing) happens before this script. See packaging/README.md for exact commands.
set -euo pipefail
source "$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<'EOF'
Usage: build-deb.sh --publish-dir DIR [options]

  --publish-dir DIR   output of `dotnet publish src/RimPulse.App -c Release -r linux-x64`
  --version VERSION   Debian package version (default: 0.0.0-dev)
  --arch ARCH         amd64 or arm64 (default: amd64)
  --runtime KIND      self-contained (default) or framework-dependent; affects Depends:
  --output DIR        destination directory (default: <repo>/artifacts/packages)

Produces rimpulse_<version>_<arch>.deb. Requires dpkg-deb on PATH.
EOF
}

for arg in "$@"; do
    if [[ "$arg" == "-h" || "$arg" == "--help" ]]; then
        usage
        exit 0
    fi
done
parse_common_args "$@"
require_command dpkg-deb

info "Publish directory: $PUBLISH_DIR"
info "Package: rimpulse $VERSION ($ARCH, $RUNTIME)"

stage="$(mktemp -d "${TMPDIR:-/tmp}/rimpulse-deb.XXXXXX")"
trap 'rm -rf -- "$stage"' EXIT

stage_payload "$PUBLISH_DIR" "$stage/opt/rimpulse"
install_desktop_assets "$stage"

mkdir -p -- "$stage/usr/bin" "$stage/usr/share/doc/rimpulse" "$stage/DEBIAN"
cp -- "$PACKAGING_LINUX_DIR/launcher-deb.sh" "$stage/usr/bin/rimpulse"
cp -- "$PACKAGING_LINUX_DIR/postinst" "$stage/DEBIAN/postinst"
cp -- "$PACKAGING_LINUX_DIR/copyright" "$stage/usr/share/doc/rimpulse/copyright"
chmod 755 -- "$stage/usr/bin/rimpulse" "$stage/DEBIAN/postinst"
chmod 644 -- "$stage/usr/share/doc/rimpulse/copyright"

render_control "$stage/DEBIAN/control" "$VERSION" "$ARCH" \
    "$(installed_size_kb "$stage")" "$(debian_depends)"

# Fail before dpkg-deb if any placeholder survived: a package with @ARCH@ in its control file is useless.
if grep -q '@[A-Z_]*@' "$stage/DEBIAN/control"; then
    die "Unsubstituted placeholder in DEBIAN/control: $(grep -o '@[A-Z_]*@' "$stage/DEBIAN/control" | tr '\n' ' ')"
fi

package="$OUTPUT_DIR/rimpulse_${VERSION}_${ARCH}.deb"
rm -f -- "$package"
dpkg-deb --build --root-owner-group -- "$stage" "$package" >/dev/null

# A last structural check: the archive must contain the launcher, the apphost and the desktop entry.
contents="$(dpkg-deb --contents "$package")"
for expected in "./usr/bin/rimpulse" "./opt/rimpulse/RimPulse" "./usr/share/applications/rimpulse.desktop"; do
    printf '%s' "$contents" | grep -qF "$expected" || die "Built package is missing '$expected'."
done
[[ "$(dpkg-deb --field "$package" Version)" == "$VERSION" ]] || die "Built package has the wrong Version field."

info "Wrote $package"
info "Install locally with: sudo apt install ./$package"
