#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_root"
bash scripts/dotnet.sh restore src/Bot --locked-mode
bash scripts/dotnet.sh build src/Bot -c Release --no-restore
# Migrations are a separate short-lived command, protected against concurrent runs.
bash scripts/dotnet.sh src/Bot/bin/Release/net10.0/Bot.dll migrate
exec bash scripts/dotnet.sh src/Bot/bin/Release/net10.0/Bot.dll serve
