#!/bin/bash
# Builds SSMP, the test bot and the standalone server for Testing/run-coop-test.sh.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="${SILKSONG_TEST_ROOT:-$HOME/Desktop/Development/silksong-test}"

dotnet build "$REPO/Testing/SSMP.TestBot/SSMP.TestBot.csproj" -c Release -v q -nologo
dotnet publish "$REPO/SSMPServer/SSMPServer.csproj" -c Release -v q -nologo -o "$ROOT/server-bin"
echo "Built SSMP, SSMP.TestBot and SSMPServer ($ROOT/server-bin)"
