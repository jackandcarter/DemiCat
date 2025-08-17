#!/usr/bin/env bash
set -euo pipefail

# Determine repository root
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")"/.. && pwd)"
cd "$ROOT_DIR"

# -----------------------------
# Python environment
# -----------------------------
PYTHON=${PYTHON:-python3}
if [ ! -d ".venv" ]; then
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
DOTNET_CMD="dotnet"
if ! command -v "$DOTNET_CMD" >/dev/null 2>&1; then
    echo "Installing .NET SDK..."
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 9.0 --install-dir "$ROOT_DIR/.dotnet"
    export PATH="$ROOT_DIR/.dotnet:$PATH"
    DOTNET_CMD="$ROOT_DIR/.dotnet/dotnet"
else
    echo ".NET SDK already installed."
fi

# Ensure local install is on PATH
if [ -d "$ROOT_DIR/.dotnet" ]; then
    export PATH="$ROOT_DIR/.dotnet:$PATH"
    DOTNET_CMD="$ROOT_DIR/.dotnet/dotnet"
fi

"$DOTNET_CMD" restore DemiCatPlugin/DemiCatPlugin.csproj
"$DOTNET_CMD" build DemiCatPlugin/DemiCatPlugin.csproj -c Release

echo "Environment setup complete."
