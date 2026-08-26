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
    DLL="$(find "$SCRIPT_DIR/bin" -name GitNext.dll | head -1)"
    exec dotnet "$DLL" --selftest "${2:-$PWD}"
fi

# Build, then run the NATIVE launcher rather than `dotnet run`. Two reasons, both
# visible on a desktop: `dotnet run` makes the process — and therefore the WM_CLASS
# instance name a shell matches an icon against — "dotnet", and it keeps the SDK in
# the process tree, so Ctrl+C and the exit code travel through a middleman. The
# apphost is emitted by every build next to the assemblies.
dotnet build "$PROJ" -v q --nologo
APP="$(find "$SCRIPT_DIR/bin" -name GitNext -type f -perm -u+x | head -1)"
if [[ -z "$APP" ]]; then
    echo "error: native launcher GitNext not found under bin/ after the build" >&2
    exit 1
fi

exec "$APP" "$@"
