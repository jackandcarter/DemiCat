#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet CLI is not available" >&2
  exit 1
fi

echo "Active dotnet SDKs:" >&2
dotnet --list-sdks >&2

if ! dotnet --list-sdks | grep -q "9\.0\.300"; then
  echo "Required SDK 9.0.300 is missing" >&2
  exit 1
fi

echo "Restoring DemiCatPlugin with SDK $(dotnet --version)" >&2
dotnet restore DemiCatPlugin/DemiCatPlugin.csproj >/dev/null

echo "SDK verification succeeded" >&2
