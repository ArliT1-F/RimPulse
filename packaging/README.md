# Packaging (milestone 7)

RimPulse is published with `dotnet publish`; **all packaging starts from a publish directory**, never from
a build output directory or a source tree. The scripts in this folder stage that output into a `.deb`, an
AppImage and a portable Windows ZIP. They need no administrator rights and do not modify the repository's
tracked files.

```text
packaging/
├── generate-icons.py           # stdlib-only rasteriser for the PNG/ICO assets; --check in CI
├── linux/
│   ├── common.sh               # shared validation, staging and metadata helpers
│   ├── build-deb.sh            # rimpulse_<version>_<arch>.deb
│   ├── build-appimage.sh       # RimPulse-<version>-<arch>.AppImage (or --appdir-only)
│   ├── verify.sh               # SDK-free smoke test run on every push
│   ├── rimpulse.desktop        # freedesktop desktop entry
│   ├── rimpulse.svg            # scalable hicolor icon
│   ├── rimpulse.png            # 256×256 hicolor icon (generated)
│   ├── control.template        # Debian control fields with @PLACEHOLDER@ values
│   ├── copyright               # DEP-5 metadata; records the missing upstream license
│   ├── postinst                # optional icon/desktop cache refresh
│   ├── launcher-deb.sh         # /usr/bin/rimpulse → /opt/rimpulse/RimPulse
│   ├── launcher-appimage.sh    # usr/bin/rimpulse inside the AppDir
│   └── AppRun                  # AppImage entry point
└── windows/
    ├── build-zip.ps1           # portable ZIP + SHA256 (PowerShell 7 or Windows PowerShell 5.1)
    ├── installer.iss           # Inno Setup 6 script (compiled on Windows, not in CI)
    └── rimpulse.ico            # PNG-compressed multi-size icon (generated)
```

## Exact commands

All paths are relative to the repository root; `artifacts/` is ignored by Git.

```bash
# 1. Publish the application (framework-dependent or self-contained).
dotnet publish src/RimPulse.App -c Release -r linux-x64 --self-contained true \
    -o artifacts/publish/linux-x64

# 2. Linux packages.
bash packaging/linux/build-deb.sh \
    --publish-dir artifacts/publish/linux-x64 --version 0.7.0 --arch amd64
bash packaging/linux/build-appimage.sh \
    --publish-dir artifacts/publish/linux-x64 --version 0.7.0 --arch amd64 \
    --appimagetool /path/to/appimagetool-x86_64.AppImage --extract-and-run

# 3. Verify the scripts and the generated metadata without publishing anything.
python3 packaging/generate-icons.py --check
bash packaging/linux/verify.sh

# 4. Windows (on Windows; prepared, not CI-built).
pwsh packaging/windows/build-zip.ps1 -PublishDir artifacts/publish/win-x64 -Version 0.7.0
iscc packaging/windows/installer.iss /DMyAppVersion=0.7.0 /DPublishDir=artifacts/publish/win-x64
```

`--appdir-only` stages the AppDir without needing `appimagetool`; it is useful for inspecting the layout
but is **not** an AppImage. `--runtime framework-dependent` changes the `.deb` dependency set to include
`dotnet-runtime-10.0`; the release workflow publishes self-contained builds, so nothing has to be
installed on the target besides the system libraries listed in `control.template`.

The release workflow (`.github/workflows/release.yml`) does all of this on `v*` tags and on manual
dispatch. It downloads `appimagetool` **1.9.1** and verifies its SHA-256 before use
(`ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0`, from the upstream release's asset
digest). Tag builds produce a **draft** GitHub Release, not a published one; see the licensing note below.

## What a package contains

| Package | Layout | Notes |
| --- | --- | --- |
| `.deb` | `/opt/rimpulse/`, `/usr/bin/rimpulse`, desktop entry, hicolor icons, `copyright`, `postinst` | Built with `dpkg-deb --build --root-owner-group`; `Depends:` is a conservative Avalonia/X11 minimum |
| AppImage | `AppRun`, `usr/lib/rimpulse/`, `usr/bin/rimpulse`, root desktop/icon/`.DirIcon` | Self-extracting, no installation; needs FUSE at run time or `APPIMAGE_EXTRACT_AND_RUN=1` |
| Windows ZIP | `RimPulse.exe` and the published files plus `README.txt` and `rimpulse.ico` | Portable, unsigned; `installer.iss` is the optional installer source |

Both Linux packages refuse a publish directory that lacks the `RimPulse` apphost,
`RimPulse.dll` or `RimPulse.runtimeconfig.json`, and refuse symlinks that escape the payload. `verify.sh`
exercises those refusals, both dependency sets, the `.deb` control archive, the AppDir layout and the
`appimagetool` invocation (with a stub tool) using a fake payload, so packaging regressions are caught on
every push without a .NET SDK.

## Verification status (read this before distributing)

| Area | Status |
| --- | --- |
| Shell scripts, icon assets, desktop/control metadata | **Verified locally and in CI** by `packaging/linux/verify.sh` against a fake payload |
| `.deb` structure and control fields | **Verified locally and in CI** with `dpkg-deb --contents` / `--field` |
| AppDir layout | **Verified locally and in CI** with `--appdir-only` |
| Real `dotnet publish` output staged into `.deb` / AppImage | **Built by the release workflow on tags**; not run on the development host |
| Installing and launching on Linux Mint 22.x (X11 and Wayland), AppImage/FUSE, icon theme integration | **Not verified.** No desktop session was available |
| Windows ZIP and Inno Setup installer | **Prepared, not built or run.** Requires a Windows host |
| Published GitHub Release | **Not published automatically** — tag builds create a draft only |

## Known blockers

1. **No upstream license.** The repository ships no `LICENSE` file. `packaging/linux/copyright` therefore
   records `License: UNKNOWN`, and the release workflow only creates a **draft** release. Choose and
   publish a project license, replace that stanza, and review the credits panel before distributing any
   package or publishing a release.
2. **Runtime dependencies are assumptions.** `libx11-6`, `libfontconfig1` and `libgl1` are the documented
   Avalonia Linux minimum, not a claim verified against every distribution. Check them on the target
   (in particular Wayland-only and minimal installs) and adjust `debian_depends` in `common.sh`.
3. **The Windows binaries are unsigned.** Expect a SmartScreen warning on first run.
4. **Packaging does not change the product.** The unfinished testing-environment and live-reload features
   (milestones 5–6) are still absent from the application; a package contains exactly what the source
   tree implements.
