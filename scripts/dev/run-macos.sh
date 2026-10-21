#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
exec "${DOTNET:-dotnet}" run --project src/DbPilot/DbPilot.csproj -- "$@"
