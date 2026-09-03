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

# The path of what the build just produced, asked of MSBuild itself.
#
# It used to be `find "$SCRIPT_DIR/bin" -name GitNext -type f -perm -u+x | head -1`, and
# that was a real defect, not a cosmetic one: every harness project that references the
# app gets its OWN copy of the app next to it, so bin/ holds fourteen of them, and `find`
# returns them in filesystem order — which is neither sorted nor stable. This script
# builds only the app project, so those copies are as old as the last time each harness
# was built (measured: two days and ten days old), and whichever one `head -1` happened to
# pick was the one that ran. A fix could therefore be built, tested, and still not be in
# the window the developer was looking at. Asking the project where its own output went
# cannot pick the wrong one.
#
# -getProperty EVALUATES, it does not build (and it exits 0 even when the code does not
# compile), so the real build above it stays exactly where it is.
target_path()
{
    dotnet build "$PROJ" -v q --nologo -getProperty:TargetPath
}

if [[ "${1:-}" == "--selftest" ]]; then
    dotnet build "$PROJ" -v q --nologo
    exec dotnet "$(target_path)" --selftest "${2:-$PWD}"
fi

# Build, then run the NATIVE launcher rather than `dotnet run`. Two reasons, both
# visible on a desktop: `dotnet run` makes the process — and therefore the WM_CLASS
# instance name a shell matches an icon against — "dotnet", and it keeps the SDK in
# the process tree, so Ctrl+C and the exit code travel through a middleman. The
# apphost is emitted by every build next to the assemblies.
dotnet build "$PROJ" -v q --nologo

# The apphost sits next to the assembly the build reported, under the same name.
APP="$(target_path)"
APP="${APP%.dll}"
if [[ ! -x "$APP" ]]; then
    echo "error: native launcher not found at '$APP' after the build" >&2
    exit 1
fi

exec "$APP" "$@"
