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
│   ├── fetch-appimagetool.sh   # downloads and SHA-256-verifies the pinned tool
│   ├── appimagetool.env        # version/URL/SHA-256 pin shared with the release workflow
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

# 2a. Get a checksum-verified appimagetool 1.9.1 (15 MB, needs network access to github.com).
#     The script prints its absolute path; build-appimage.sh finds that default location automatically.
bash packaging/linux/fetch-appimagetool.sh
bash packaging/linux/build-appimage.sh \
    --publish-dir artifacts/publish/linux-x64 --version 0.7.0 --arch amd64 --extract-and-run

#     If you keep the tool elsewhere, pass its real path instead (never the docs placeholder):
#   bash packaging/linux/build-appimage.sh ... --appimagetool "$HOME/Downloads/appimagetool-x86_64.AppImage"

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

If the download is blocked by a proxy, fetch `appimagetool-x86_64.AppImage` from the 1.9.1 release page by
hand, check its SHA-256 against `packaging/linux/appimagetool.env`, and pass its real path via
`--appimagetool`. GitHub redirects release assets to `release-assets.githubusercontent.com`, so a
firewall that only allows `github.com` will fail the download.

`fetch-appimagetool.sh` reads `packaging/linux/appimagetool.env`, which pins stable `appimagetool` **1.9.1**
URLs and SHA-256 values for both `x86_64` and `aarch64` (taken from the upstream release asset digests).
It reuses a checksum-valid copy, re-downloads a bad one, refuses a mismatched download, and writes only
the resolved path to stdout. `--check` validates the pin offline; `--arch arm64` selects the aarch64 tool.

The release workflow (`.github/workflows/release.yml`) does all of this on `v*` tags and on manual
dispatch: it calls the very same `fetch-appimagetool.sh`, so CI and a local build verify exactly the same
binary. Tag builds produce a **draft** GitHub Release, not a published one; see the licensing note below.

## What a package contains

| Package | Layout | Notes |
| --- | --- | --- |
| `.deb` | `/opt/rimpulse/`, `/usr/bin/rimpulse`, desktop entry, hicolor icons, `copyright`, `postinst` | Built with `dpkg-deb --build --root-owner-group`; `Depends:` is a conservative Avalonia/X11 minimum |
| AppImage | `AppRun`, `usr/lib/rimpulse/`, `usr/bin/rimpulse`, root desktop/icon/`.DirIcon` | Self-extracting, no installation; needs FUSE at run time or `APPIMAGE_EXTRACT_AND_RUN=1` |
| Windows ZIP | `RimPulse.exe` and the published files plus `README.txt` and `rimpulse.ico` | Portable, unsigned; `installer.iss` is the optional installer source |

Both Linux packages refuse a publish directory that lacks the `RimPulse` apphost, `RimPulse.dll` or both
dependency manifests, and refuse symlinks that escape the payload. `verify.sh`
exercises those refusals, both dependency sets, the `.deb` control archive, the AppDir layout and the
`appimagetool` invocation (with a stub tool) using a fake payload, so packaging regressions are caught on
every push without a .NET SDK.

## Verification status (read this before distributing)

| Area | Status |
| --- | --- |
| Shell scripts, icon assets, desktop/control metadata | **Verified locally and in CI** by `packaging/linux/verify.sh` against a fake payload |
| `.deb` structure and control fields | **Verified locally and in CI** with `dpkg-deb --contents` / `--field` |
| AppDir layout | **Verified locally and in CI** with `--appdir-only` |
| Real `dotnet publish` output staged into a `.deb` | **User-reported success** | A user ran `dotnet publish src/RimPulse.App -c Release -r linux-x64 --self-contained true` and `build-deb.sh --version 0.7.0 --arch amd64` on their machine; both succeeded. Installing or launching that `.deb` was not reported. The release workflow also builds it on tags. |
| Real `dotnet publish` output staged into an AppImage | **Not yet verified** | Built by the release workflow on tags; no real AppImage has been built or run on a desktop here. |
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
