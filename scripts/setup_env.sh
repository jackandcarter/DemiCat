#!/usr/bin/env bash
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage: setup_env.sh [--unit-tests] [--integration-tests]

Bootstraps the development environment by ensuring Python 3.11+ and the
latest .NET SDK are available, creating a virtual environment, installing
Python dependencies, and building the Dalamud plugin.

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
if ! command -v "$PYTHON" >/dev/null 2>&1 || ! "$PYTHON" -c 'import sys; exit(0 if sys.version_info >= (3,11) else 1)'; then
    echo "Python 3.11+ not found. Attempting installation..."
    if command -v uv >/dev/null 2>&1; then
        uv python install 3.11
        PYTHON="$(uv python find 3.11)"
    elif command -v brew >/dev/null 2>&1; then
        brew install python@3.11
        PYTHON="$(brew --prefix python@3.11)/bin/python3.11"
    else
        echo "Neither 'uv' nor 'brew' is available to install Python." >&2
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
if command -v dotnet >/dev/null 2>&1; then
    DOTNET_CMD="$(command -v dotnet)"
else
    echo ".NET SDK not found. Attempting installation..."
    if command -v brew >/dev/null 2>&1; then
        brew install dotnet-sdk
        DOTNET_CMD="$(command -v dotnet)"
    else
        curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
        bash /tmp/dotnet-install.sh --version latest --install-dir "$ROOT_DIR/.dotnet"
        DOTNET_CMD="$ROOT_DIR/.dotnet/dotnet"
        export PATH="$ROOT_DIR/.dotnet:$PATH"
    fi
fi

"$DOTNET_CMD" restore DemiCatPlugin/DemiCatPlugin.csproj
"$DOTNET_CMD" build DemiCatPlugin/DemiCatPlugin.csproj -c Release

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

