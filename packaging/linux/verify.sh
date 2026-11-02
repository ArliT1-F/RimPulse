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

# 3b. The appimagetool pin must be well formed and shared with CI, and the docs must not tell users to
# paste a placeholder path (the exact mistake this increment fixes).
bash "$PACKAGING_LINUX_DIR/fetch-appimagetool.sh" --check >/dev/null 2>&1 ||
    fail "fetch-appimagetool.sh --check rejected the amd64 pin"
bash "$PACKAGING_LINUX_DIR/fetch-appimagetool.sh" --check --arch arm64 >/dev/null 2>&1 ||
    fail "fetch-appimagetool.sh --check rejected the aarch64 pin"
bash "$PACKAGING_LINUX_DIR/fetch-appimagetool.sh" --help >/dev/null 2>&1 ||
    fail "fetch-appimagetool.sh --help failed"
grep -qF 'fetch-appimagetool.sh' "$REPO_ROOT/.github/workflows/release.yml" ||
    fail "the release workflow no longer uses the shared appimagetool pin"
for doc in README.md DEVELOPMENT.md packaging/README.md; do
    if grep -qF '/path/to/appimagetool' "$REPO_ROOT/$doc"; then
        fail "$doc still tells users to pass a placeholder appimagetool path"
    fi
done
pass "appimagetool pin, CI wiring and documentation commands"

# 4. End-to-end .deb from a fake publish payload.
work="$(mktemp -d "${TMPDIR:-/tmp}/rimpulse-verify.XXXXXX")"
tools_dir="$REPO_ROOT/artifacts/tools"
created_tool=0
trap 'rm -rf -- "$work"; if [[ "$created_tool" == "1" ]]; then rm -f -- "$tools_dir/appimagetool-x86_64.AppImage"; fi' EXIT
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
deb_output="$("$PACKAGING_LINUX_DIR/build-deb.sh" --publish-dir "$publish" --version 0.0.0-test \
    --arch amd64 --runtime self-contained --output "$output" 2>&1)" || fail "build-deb.sh failed"
deb="$output/rimpulse_0.0.0-test_amd64.deb"
# The install hint must be a command a user can paste: no "./" prefixed to an absolute path.
if printf '%s' "$deb_output" | grep -qF './/'; then
    fail "build-deb.sh printed an unusable './'+absolute install path"
fi
printf '%s' "$deb_output" | grep -qF "sudo apt install \"$deb\"" ||
    fail "build-deb.sh install hint does not contain the absolute package path"
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

# 5b. Exercise the real appimagetool code path with a stub. The tag workflow supplies the pinned real
# tool; this checks the invocation (ARCH, flags, arguments) and the produced image without a download.
fake_tool="$work/fake-appimagetool"
cat >"$fake_tool" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf 'ARCH=%s\n' "${ARCH:-}" >"$FAKE_LOG"
last=""
for arg in "$@"; do
    printf '%s\n' "$arg" >>"$FAKE_LOG"
    last="$arg"
done
printf 'fake appimage\n' >"$last"
EOF
chmod 755 -- "$fake_tool"
fake_log="$work/appimagetool.log"
FAKE_LOG="$fake_log" "$PACKAGING_LINUX_DIR/build-appimage.sh" --publish-dir "$publish" \
    --version 0.0.0-test --arch amd64 --output "$output" \
    --appimagetool "$fake_tool" --extract-and-run >/dev/null || fail "AppImage build path failed"
image="$output/RimPulse-0.0.0-test-x86_64.AppImage"
[[ -s "$image" && -x "$image" ]] || fail "no executable AppImage was produced"
grep -qx 'ARCH=x86_64' "$fake_log" || fail "appimagetool was not invoked with ARCH=x86_64"
for expected in '--appimage-extract-and-run' '--no-appstream' 'RimPulse-0.0.0-test-amd64.AppDir' "$image"; do
    grep -qF -- "$expected" "$fake_log" || fail "appimagetool invocation lost '$expected'"
done
pass "appimagetool invocation and resulting image are correct (stub tool)"

# 5c. A tool in fetch-appimagetool.sh's default location is auto-detected, so the common flow needs no
# --appimagetool argument. Skip this assertion on a host that already has appimagetool on PATH (that
# takes precedence by design).
if [[ -z "$(command -v appimagetool || true)" && ! -e "$tools_dir/appimagetool-x86_64.AppImage" ]]; then
    mkdir -p -- "$tools_dir"
    cp -- "$fake_tool" "$tools_dir/appimagetool-x86_64.AppImage"
    chmod 755 -- "$tools_dir/appimagetool-x86_64.AppImage"
    created_tool=1
    auto_log="$work/auto-detected.log"
    rm -f -- "$image"
    FAKE_LOG="$auto_log" "$PACKAGING_LINUX_DIR/build-appimage.sh" --publish-dir "$publish" \
        --version 0.0.0-test --arch amd64 --output "$output" --extract-and-run >/dev/null ||
        fail "build-appimage.sh did not auto-detect the fetched tool"
    [[ -s "$image" ]] || fail "auto-detected tool produced no AppImage"
    grep -qx 'ARCH=x86_64' "$auto_log" || fail "the auto-detected stub tool did not run"
    pass "appimagetool is auto-detected from artifacts/tools"
fi

# A pasted example path must be refused with an actionable message, not a bare "not executable".
if "$PACKAGING_LINUX_DIR/build-appimage.sh" --publish-dir "$publish" --version 0.0.0-test \
    --arch amd64 --output "$output" --appimagetool /path/to/appimagetool-x86_64.AppImage \
    --extract-and-run >"$work/placeholder.log" 2>&1; then
    fail "build-appimage.sh accepted a placeholder appimagetool path"
fi
grep -qF 'placeholder from the docs' "$work/placeholder.log" ||
    fail "the placeholder error does not explain how to get a real appimagetool"
pass "placeholder appimagetool paths are refused with guidance"

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
