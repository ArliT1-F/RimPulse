#!/bin/sh
# Installed as /usr/bin/rimpulse by build-deb.sh. Kept tiny so there is only one place that knows
# where the published application lives.
set -eu
exec /opt/rimpulse/RimPulse "$@"
