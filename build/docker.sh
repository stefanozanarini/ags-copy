#!/bin/sh
# Runs a dotnet command in the official .NET 8 SDK container (no SDK needed on the host).
#   build/docker.sh build -c Release
#   build/docker.sh test tests/AgsCopy.Core.Tests
#   build/docker.sh publish src/AgsCopy.App -c Release -o publish/app
# Files created in the container are handed back to the calling user.
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
docker run --rm --network host \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -v "$ROOT":/src -v ags-copy-nuget:/root/.nuget -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  sh -c "dotnet $*; rc=\$?; chown -R $(id -u):$(id -g) /src; exit \$rc"
