#!/bin/sh
# Builds the Windows package in publish/AGS-Copy: service, watchdog, GUI and the .NET runtime
# (self-contained, so target machines need nothing installed), plus the install scripts.
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=publish/AGS-Copy
rm -rf "$ROOT/$OUT"
"$ROOT/build/docker.sh" "publish src/AgsCopy.Service -c Release -r win-x64 --self-contained -o $OUT && dotnet publish src/AgsCopy.Watchdog -c Release -r win-x64 --self-contained -o $OUT && dotnet publish src/AgsCopy.App -c Release -r win-x64 --self-contained -o $OUT"
cp "$ROOT/installer/install.ps1" "$ROOT/installer/uninstall.ps1" "$ROOT/$OUT/"
rm -f "$ROOT/$OUT"/*.pdb
echo "Package ready in $OUT"
