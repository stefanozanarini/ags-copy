#!/bin/sh
# Builds publish/AGS-Copy-Setup-<version>.exe from publish/AGS-Copy (run build/publish.sh first,
# or pass --publish). NSIS runs in a Debian container, so nothing is installed on the host.
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
[ "$1" = "--publish" ] && "$ROOT/build/publish.sh"
[ -f "$ROOT/publish/AGS-Copy/AgsCopy.Service.exe" ] || { echo "publish/AGS-Copy missing: run build/publish.sh" >&2; exit 1; }

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")
OUT="publish/AGS-Copy-Setup-$VERSION.exe"
docker run --rm --network host -v "$ROOT":/src -w /src debian:12 sh -c "
  apt-get update -qq >/dev/null && apt-get install -y -qq nsis >/dev/null &&
  makensis -V2 -DVERSION=$VERSION -DSOURCE=/src/publish/AGS-Copy -DOUTFILE=/src/$OUT installer/ags-copy.nsi;
  rc=\$?; chown $(id -u):$(id -g) /src/$OUT 2>/dev/null; exit \$rc"
ls -lh "$ROOT/$OUT"
