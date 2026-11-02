#!/usr/bin/env bash
# Packaging smoke test. It does not need the .NET SDK or a real publish: a fake payload exercises the
# staging, control-file rendering, .deb assembly and AppDir layout on every push. It does NOT prove that
# a real published RimPulse starts under a desktop session; see packaging/README.md.
set -euo pipefail

PACKAGING_LINUX_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PACKAGING_DIR="$(dirname -- "$PACKAGING_LINUX_DIR")"
REPO_ROOT="$(dirname -- "$PACKAGING_DIR")"

pass() { printf '  PASS  %s\n' "$*"; }
fail() { printf '  FAIL  %s\n' "$*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "required command '$1' is missing"; }

require_command bash
require_command python3
require_command dpkg-deb
require_command find
require_command readlink
require_command tar

printf 'RimPulse packaging smoke test\n'

# 1. Shell syntax, including the launcher scripts that are copied into the packages.
while IFS= read -r -d '' script; do
    bash -n "$script" || fail "shell syntax: $script"
done < <(find "$PACKAGING_DIR" -type f -name '*.sh' -print0)
bash -n "$PACKAGING_LINUX_DIR/AppRun" || fail "shell syntax: AppRun"
bash -n "$PACKAGING_LINUX_DIR/launcher-deb.sh" || fail "shell syntax: launcher-deb.sh"
bash -n "$PACKAGING_LINUX_DIR/launcher-appimage.sh" || fail "shell syntax: launcher-appimage.sh"
pass "all shell scripts parse"

# 2. Binary assets must match their generator (no drift, no accidental replacement).
python3 "$PACKAGING_DIR/generate-icons.py" --check >/dev/null || fail "icon assets are stale"
python3 - "$PACKAGING_LINUX_DIR/rimpulse.png" "$PACKAGING_DIR/windows/rimpulse.ico" <<'PY' || fail "icon file headers"
import sys
from pathlib import Path
png = Path(sys.argv[1]).read_bytes()
ico = Path(sys.argv[2]).read_bytes()
assert png.startswith(b"\x89PNG\r\n\x1a\n"), "rimpulse.png is not a PNG"
assert ico[:6] == b"\x00\x00\x01\x00" + (6).to_bytes(2, "little"), "rimpulse.ico has a bad ICONDIR"
assert b"\x89PNG\r\n\x1a\n" in ico, "rimpulse.ico contains no PNG image"
PY
pass "icon assets match the generator and have valid headers"

# 3. Static metadata.
desktop="$PACKAGING_LINUX_DIR/rimpulse.desktop"
for field in 'Type=Application' 'Name=RimPulse' 'Exec=rimpulse' 'Icon=rimpulse' 'Terminal=false' 'Categories='; do
    grep -q "^$field" "$desktop" || fail "desktop entry is missing $field"
done
grep -q '<svg' "$PACKAGING_LINUX_DIR/rimpulse.svg" || fail "SVG icon is missing"
for placeholder in '@VERSION@' '@ARCH@' '@DEPENDS@' '@INSTALLED_SIZE@'; do
    grep -qF "$placeholder" "$PACKAGING_LINUX_DIR/control.template" || fail "control template lost $placeholder"
done
pass "desktop entry, SVG icon and control template are complete"

# 4. End-to-end .deb from a fake publish payload.
work="$(mktemp -d "${TMPDIR:-/tmp}/rimpulse-verify.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT
publish="$work/publish"
mkdir -p -- "$publish"
cat >"$publish/RimPulse" <<'EOF'
#!/bin/sh
echo fake-apphost
EOF
chmod 755 -- "$publish/RimPulse"
printf 'fake managed assembly\n' >"$publish/RimPulse.dll"
printf '{"runtimeOptions":{}}\n' >"$publish/RimPulse.runtimeconfig.json"
printf '{"runtimeTarget":{}}\n' >"$publish/RimPulse.deps.json"
printf 'local data\n' >"$publish/README.txt"
ln -s README.txt "$publish/linked-readme.txt" # a symlink that stays inside the payload is fine

output="$work/out"
"$PACKAGING_LINUX_DIR/build-deb.sh" --publish-dir "$publish" --version 0.0.0-test --arch amd64 \
    --runtime self-contained --output "$output" >/dev/null || fail "build-deb.sh failed"
deb="$output/rimpulse_0.0.0-test_amd64.deb"
[[ -s "$deb" ]] || fail "no .deb was produced"
[[ "$(dpkg-deb --field "$deb" Package)" == "rimpulse" ]] || fail "wrong Package field"
[[ "$(dpkg-deb --field "$deb" Version)" == "0.0.0-test" ]] || fail "wrong Version field"
[[ "$(dpkg-deb --field "$deb" Architecture)" == "amd64" ]] || fail "wrong Architecture field"
[[ "$(dpkg-deb --field "$deb" Depends)" == *"libx11-6"* ]] || fail "missing X11 dependency"
[[ "$(dpkg-deb --field "$deb" Depends)" != *"dotnet-runtime-10.0"* ]] || fail "self-contained .deb must not require dotnet-runtime"
contents="$(dpkg-deb --contents "$deb")"
for expected in './opt/rimpulse/RimPulse' './opt/rimpulse/RimPulse.dll' './usr/bin/rimpulse' \
    './usr/share/applications/rimpulse.desktop' \
    './usr/share/icons/hicolor/256x256/apps/rimpulse.png' \
    './usr/share/doc/rimpulse/copyright'; do
    printf '%s' "$contents" | grep -qF "$expected" || fail ".deb is missing $expected"
done
# Maintainer scripts live in the control archive, not the data archive.
control_members="$(dpkg-deb --ctrl-tarfile "$deb" | tar -tf -)"
for expected in './control' './postinst'; do
    printf '%s' "$control_members" | grep -qF "$expected" || fail ".deb control archive is missing $expected"
done
control_text="$(dpkg-deb --ctrl-tarfile "$deb" | tar -xOf - ./control)"
if printf '%s' "$control_text" | grep -q '@ARCH@'; then
    fail "control placeholder leaked into the .deb"
fi
printf '%s' "$control_text" | grep -q '^Installed-Size: [1-9]' || fail "Installed-Size is missing or zero"
pass ".deb builds with the expected layout and metadata"

# A framework-dependent publish produces a different dependency set.
"$PACKAGING_LINUX_DIR/build-deb.sh" --publish-dir "$publish" --version 0.0.0-test --arch arm64 \
    --runtime framework-dependent --output "$output" >/dev/null || fail "framework-dependent .deb failed"
[[ "$(dpkg-deb --field "$output/rimpulse_0.0.0-test_arm64.deb" Depends)" == *"dotnet-runtime-10.0"* ]] ||
    fail "framework-dependent .deb is missing dotnet-runtime-10.0"
pass "framework-dependent .deb declares the .NET runtime"

# 5. AppDir staging (no appimagetool needed here; the release workflow supplies a pinned one).
"$PACKAGING_LINUX_DIR/build-appimage.sh" --publish-dir "$publish" --version 0.0.0-test --arch amd64 \
    --output "$output" --appdir-only >/dev/null || fail "build-appimage.sh --appdir-only failed"
appdir="$output/RimPulse-0.0.0-test-amd64.AppDir"
for expected in AppRun rimpulse.desktop rimpulse.svg rimpulse.png .DirIcon usr/bin/rimpulse usr/lib/rimpulse/RimPulse; do
    [[ -e "$appdir/$expected" ]] || fail "AppDir is missing $expected"
done
[[ -x "$appdir/AppRun" ]] || fail "AppRun is not executable"
[[ -x "$appdir/usr/bin/rimpulse" ]] || fail "AppImage launcher is not executable"
pass "AppDir has the expected layout and executables"

# 6. Refusals: an unrelated directory and a payload with an escaping symlink must not be packaged.
if "$PACKAGING_LINUX_DIR/build-deb.sh" --publish-dir "$work" --version 0.0.0-test --output "$output" \
    >/dev/null 2>&1; then
    fail "build-deb.sh accepted a directory that is not a publish output"
fi
bad="$work/bad-publish"
cp -a -- "$publish" "$bad"
ln -sf /etc/hostname "$bad/escaping-link"
if "$PACKAGING_LINUX_DIR/build-deb.sh" --publish-dir "$bad" --version 0.0.0-test --output "$output" \
    >/dev/null 2>&1; then
    fail "build-deb.sh accepted a symlink escaping the payload"
fi
if "$PACKAGING_LINUX_DIR/build-deb.sh" --publish-dir "$publish" --version 'not a version!' --output "$output" \
    >/dev/null 2>&1; then
    fail "build-deb.sh accepted an invalid Debian version"
fi
no_manifest="$work/no-manifest"
mkdir -p -- "$no_manifest"
cp -- "$publish/RimPulse" "$no_manifest/RimPulse"
cp -- "$publish/RimPulse.dll" "$no_manifest/RimPulse.dll"
if "$PACKAGING_LINUX_DIR/build-deb.sh" --publish-dir "$no_manifest" --version 0.0.0-test \
    --output "$output" >/dev/null 2>&1; then
    fail "build-deb.sh accepted a payload with no dependency/runtime manifest"
fi
pass "invalid publish outputs, escaping symlinks, manifests and versions are refused"

printf 'Packaging smoke test passed.\n'
