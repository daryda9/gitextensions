#!/usr/bin/env bash
# Build (if needed) and launch the Linux/Avalonia Git Extensions.
#
# Usage:
#   ./run.sh                 # GUI, opens the current directory
#   ./run.sh /path/to/repo   # GUI, opens the given repo
#   ./run.sh --selftest [repo]   # headless: print branch + commit log, no display
set -euo pipefail

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$SCRIPT_DIR/App/GitExtensions.Avalonia.csproj"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found. Expected .NET 10 SDK at $DOTNET_ROOT" >&2
    exit 1
fi

if [[ "${1:-}" == "--selftest" ]]; then
    dotnet build "$PROJ" -v q --nologo
    DLL="$(find "$SCRIPT_DIR/bin" -name GitExtensions.Avalonia.dll | head -1)"
    exec dotnet "$DLL" --selftest "${2:-$PWD}"
fi

exec dotnet run --project "$PROJ" -v q --nologo -- "$@"
