#!/bin/sh
# Installed as usr/bin/rimpulse inside the AppDir. The AppImage is mounted at an arbitrary path, so the
# application root must be resolved relative to this script at run time.
set -eu
HERE="$(CDPATH= cd -- "$(dirname -- "$(readlink -f -- "$0")")" && pwd)"
APPDIR="$(dirname -- "$HERE")"
exec "$APPDIR/usr/lib/rimpulse/RimPulse" "$@"
