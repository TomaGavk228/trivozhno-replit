#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_root"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
if command -v dotnet >/dev/null 2>&1 && dotnet --version >/dev/null 2>&1; then
  exec dotnet "$@"
fi
export DOTNET_ROOT="$project_root/.dotnet"
if [[ ! -x "$DOTNET_ROOT/dotnet" ]]; then
  printf '%s\n' 'Перший запуск: завантаження .NET SDK 10.0.401…'
  sdk_arch="$(uname -m)"
  case "$sdk_arch" in x86_64) sdk_arch=x64;; aarch64|arm64) sdk_arch=arm64;; *) printf '%s\n' 'Потрібен Linux x64 або arm64.' >&2; exit 1;; esac
  mkdir -p "$DOTNET_ROOT"
  sdk_archive="$(mktemp)"
  trap 'rm -f "$sdk_archive"' EXIT
  curl --fail --location --retry 3 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-'"$sdk_arch"'.tar.gz' -o "$sdk_archive"
  tar --no-same-owner -xzf "$sdk_archive" -C "$DOTNET_ROOT"
  rm -f "$sdk_archive"
  trap - EXIT
fi
export PATH="$DOTNET_ROOT:$PATH"
exec "$DOTNET_ROOT/dotnet" "$@"
