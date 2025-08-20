#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: setup_env.sh [--unit-tests] [--integration-tests]

Bootstraps the development environment by ensuring Python 3.11+ and the
latest .NET SDK are available, creating a virtual environment, installing
Python dependencies, and building the Dalamud plugin.

Requires one of: 'uv', 'brew', or 'apt' to install Python if necessary.

Optional flags:
  --unit-tests          Run unit tests (pytest -m "not integration")
  --integration-tests   Run integration tests (pytest -m integration)
USAGE
}

run_unit=false
run_integration=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --unit-tests) run_unit=true; shift ;;
        --integration-tests) run_integration=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

# Determine repository root
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")"/.. && pwd)"
cd "$ROOT_DIR"

# -----------------------------
# Python 3.11+ detection/installation
# -----------------------------
PYTHON=${PYTHON:-python3}
if ! command -v "$PYTHON" >/dev/null 2>&1; then
    echo "Python 3.11+ not found. Attempting installation..."
    if command -v uv >/dev/null 2>&1; then
        uv python install 3.11
        PYTHON="$(uv python find 3.11)"
    elif command -v brew >/dev/null 2>&1; then
        brew install python@3.11
        PYTHON="$(brew --prefix python@3.11)/bin/python3.11"
    elif command -v apt-get >/dev/null 2>&1; then
        sudo apt-get update
        sudo apt-get install -y python3.11 python3.11-venv
        PYTHON="$(command -v python3.11)"
    else
        echo "Neither 'uv', 'brew', nor 'apt' is available to install Python." >&2
        exit 1
    fi
elif ! "$PYTHON" -c 'import sys; exit(0 if sys.version_info >= (3,11) else 1)'; then
    echo "$("$PYTHON" -V 2>&1) detected, but Python 3.11+ is required. Attempting installation..."
    if command -v uv >/dev/null 2>&1; then
        uv python install 3.11
        PYTHON="$(uv python find 3.11)"
    elif command -v brew >/dev/null 2>&1; then
        brew install python@3.11
        PYTHON="$(brew --prefix python@3.11)/bin/python3.11"
    elif command -v apt-get >/dev/null 2>&1; then
        sudo apt-get update
        sudo apt-get install -y python3.11 python3.11-venv
        PYTHON="$(command -v python3.11)"
    else
        echo "Automatic upgrade unavailable. Please install Python 3.11+ manually." >&2
        exit 1
    fi
fi

# -----------------------------
# Virtual environment and dependencies
# -----------------------------
if [ ! -d .venv ]; then
    "$PYTHON" -m venv .venv
fi
# shellcheck disable=SC1091
source .venv/bin/activate
pip install --upgrade pip

# Install dependencies from demibot/pyproject.toml
python <<'PY'
import tomllib, subprocess
from pathlib import Path

toml_path = Path('demibot/pyproject.toml')
data = tomllib.load(toml_path.open('rb'))

def normalize(ver: str) -> str:
    return ver.replace('^', '>=') if ver else ''

def flatten(prefix, value):
    if isinstance(value, dict) and not {'version', 'extras', 'optional'} & value.keys():
        for k, v in value.items():
            yield from flatten(f"{prefix}.{k}" if prefix else k, v)
    else:
        yield prefix, value

deps = []
for name, spec in flatten('', data.get('project', {}).get('dependencies', {})):
    if isinstance(spec, str):
        deps.append(f"{name}{normalize(spec)}")
    elif isinstance(spec, dict):
        extras = '[' + ','.join(spec.get('extras', [])) + ']' if spec.get('extras') else ''
        version = normalize(spec.get('version', ''))
        deps.append(f"{name}{extras}{version}")
    else:
        deps.append(name)
if deps:
    subprocess.check_call(['pip', 'install', *deps])
PY

# -----------------------------
# .NET SDK and plugin build
# -----------------------------
install_dotnet() {
    if command -v brew >/dev/null 2>&1; then
        brew install dotnet-sdk
        DOTNET_CMD="$(command -v dotnet)"
    else
        curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --version 9.0.100 --install-dir "$ROOT_DIR/.dotnet"
        DOTNET_CMD="$ROOT_DIR/.dotnet/dotnet"
        export PATH="$ROOT_DIR/.dotnet:$PATH"
    fi
}

REQUIRED_DOTNET_MAJOR=9
if command -v dotnet >/dev/null 2>&1; then
    DOTNET_CMD="$(command -v dotnet)"
    DOTNET_VERSION="$($DOTNET_CMD --version)"
    DOTNET_MAJOR="${DOTNET_VERSION%%.*}"
    if (( DOTNET_MAJOR < REQUIRED_DOTNET_MAJOR )); then
        echo ".NET SDK $DOTNET_VERSION detected, but $REQUIRED_DOTNET_MAJOR.x or newer is required. Attempting installation..."
        install_dotnet
    fi
else
    echo ".NET SDK not found. Attempting installation..."
    install_dotnet
fi

if ! "$DOTNET_CMD" nuget list source | grep -q Dalamud; then
    if [[ -n "${GITHUB_USERNAME:-}" && -n "${GITHUB_TOKEN:-}" ]]; then
        "$DOTNET_CMD" nuget add source https://nuget.pkg.github.com/goatcorp/index.json --name Dalamud --username "$GITHUB_USERNAME" --password "$GITHUB_TOKEN" --store-password-in-clear-text
    else
        "$DOTNET_CMD" nuget add source https://nuget.pkg.github.com/goatcorp/index.json --name Dalamud
    fi
fi

"$DOTNET_CMD" restore DemiCatPlugin/DemiCatPlugin.csproj
"$DOTNET_CMD" build DemiCatPlugin/DemiCatPlugin.csproj -c Release

# Run .NET tests
find tests -name '*Tests.csproj' -print0 | while IFS= read -r -d '' testproj; do
    "$DOTNET_CMD" test "$testproj" --configuration Release --no-build
done

# -----------------------------
# Optional tests
# -----------------------------
if $run_unit; then
    pytest -m "not integration"
fi

if $run_integration; then
    pytest -m integration
fi

echo "Environment setup complete."

