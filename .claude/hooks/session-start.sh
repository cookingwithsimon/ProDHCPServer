#!/bin/bash
# Prepares a Claude Code cloud session so it can build and run the Core tests.
# Local machines are left alone: there you install the .NET 10 SDK yourself.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# The dot.net install script is blocked in cloud sessions; the Ubuntu feed works.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  SUDO=""
  if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi
  $SUDO apt-get update -qq
  $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0 >/dev/null
fi

{
  echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
  echo 'export DOTNET_NOLOGO=1'
  echo 'export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1'
} >> "${CLAUDE_ENV_FILE:-/dev/null}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}"

# Restore NuGet packages so the first build/test in the session is quick.
# WPF and Windows projects restore fine on Linux thanks to EnableWindowsTargeting.
if [ -f ProDHCPServer.sln ]; then
  dotnet restore ProDHCPServer.sln --verbosity quiet
fi
