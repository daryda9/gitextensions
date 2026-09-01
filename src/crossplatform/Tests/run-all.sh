#!/usr/bin/env bash
# Builds the port's solution and runs every deterministic harness under Tests/.
#
# Usage:
#   Tests/run-all.sh              # build, then run the deterministic harnesses
#   Tests/run-all.sh --no-build   # run what is already in bin/ (faster iteration)
#   Tests/run-all.sh --list       # print what would run, and what is excluded
#
# Exit code 0 means every harness printed its PASS line and exited 0. Any other
# value means at least one did not, and its whole output is reprinted at the end.
#
# WHY THIS EXISTS. The harnesses are ordinary console executables that assert and
# exit non-zero, and until this script there was nothing that ran them: each was
# started by hand, on the day it was written, and then usually never again. A
# suite nobody runs is not a safety net — it is a file that happens to compile.
# One of them has already caught a defect in code that had shipped the day
# before, which is exactly the case a by-hand ritual misses.
#
# Every harness runs in a sandbox of its own:
#   * XDG_CONFIG_HOME points inside a scratch directory, so a suite that writes
#     settings can never touch the developer's real ~/.config — several of these
#     deliberately corrupt, hammer and SIGKILL their settings files;
#   * TMPDIR likewise, so two runs (or two CI jobs) cannot collide over the fixed
#     sandbox paths some harnesses build under the temp directory;
#   * GIT_CONFIG_GLOBAL/SYSTEM are silenced, so a developer's commit.gpgsign or
#     init.defaultBranch cannot decide whether the git-backed harnesses pass.
# The scratch root is removed on success and kept on failure, where it is the
# evidence.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
SOLUTION="$ROOT/GitExtensions.Avalonia.slnx"
BIN="$ROOT/bin"
CONFIGURATION="${CONFIGURATION:-Debug}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

# name|assembly|timeout seconds|arguments
#
# The timeouts are per harness and generous — roughly ten times the measured run
# — because their job is to turn a deadlock into a failure, not to police speed.
HARNESSES=(
    "navigation-snapshot|NavigationSnapshot.Harness|120|"
    "submodule-hierarchy|SubmoduleHierarchy.Harness|180|"
    "view-prefs|ViewPrefsRegression.Harness|300|"
    "settings-stores|SettingsStoresRegression.Harness|300|"
    "image-integrity|ImageIntegrityRegression.Harness|180|"
    "inline-diff|InlineDiffRegression.Harness|300|"
    "command-palette|CommandPaletteRegression.Harness|300|"
    "syntax-tokenize|SyntaxTokenizeRegression.Harness|120|"
    "merge-resolve|MergeResolveRegression.Harness|180|"
    "asset-names|AssetNamesRegression.Harness|60|$ROOT/App"
)

# Deliberately not run here, with the reason, so their absence reads as a
# decision rather than an oversight:
#
#   AnimProbe, ChromeProbe  open a window: they need a display, and what they
#                           report is measured pixels rather than a verdict.
#   Perf                    measures wall-clock against a real repository given
#                           on the command line; a timing is not a pass or a
#                           fail, and on shared CI hardware it is not even
#                           comparable between runs.

usage()
{
    sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

build=1
for arg in "$@"; do
    case "$arg" in
        --no-build) build=0 ;;
        --list)
            echo "Runs:"
            for entry in "${HARNESSES[@]}"; do echo "  ${entry%%|*}"; done
            echo "Excluded: anim-probe, chrome-probe (need a display), perf (a measurement, not a verdict)"
            exit 0
            ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown argument: $arg" >&2; usage >&2; exit 2 ;;
    esac
done

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found. Expected the .NET 10 SDK at $DOTNET_ROOT" >&2
    exit 2
fi

if [[ $build -eq 1 ]]; then
    echo "==> building $(basename "$SOLUTION") ($CONFIGURATION)"
    # UseSharedCompilation=false: the persistent compiler server survives the run
    # and has been seen holding stale references to just-rebuilt assemblies.
    if ! dotnet build "$SOLUTION" -c "$CONFIGURATION" -p:UseSharedCompilation=false -v q --nologo; then
        echo "BUILD FAILED" >&2
        exit 1
    fi
fi

# A harness fails by throwing, and .NET writes a core dump for an unhandled
# exception. The dump carries nothing the printed assertion does not, costs
# seconds of disk on a machine running systemd-coredump, and — observed once
# here — was still being written while the next harness in the loop started,
# which killed that one too and reported it as a failure with no output.
ulimit -c 0 2>/dev/null || true

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/ge-harness-XXXXXXXX")"
declare -a FAILED=()
declare -a TIMINGS=()

for entry in "${HARNESSES[@]}"; do
    IFS='|' read -r name assembly limit harness_args <<<"$entry"
    dll="$BIN/$assembly/$CONFIGURATION/net10.0/$assembly.dll"

    if [[ ! -f "$dll" ]]; then
        echo "==> $name: MISSING $dll (build first, or drop --no-build)"
        FAILED+=("$name")
        continue
    fi

    sandbox="$SCRATCH/$name"
    mkdir -p "$sandbox/config" "$sandbox/tmp"
    log="$SCRATCH/$name.log"

    started=$SECONDS
    # `timeout --kill-after` because a harness that hangs holding a file lock
    # ignores SIGTERM in exactly the case worth catching.
    env -i \
        HOME="$HOME" \
        PATH="$PATH" \
        DOTNET_ROOT="$DOTNET_ROOT" \
        DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        DOTNET_NOLOGO=1 \
        XDG_CONFIG_HOME="$sandbox/config" \
        TMPDIR="$sandbox/tmp" \
        GIT_CONFIG_GLOBAL=/dev/null \
        GIT_CONFIG_SYSTEM=/dev/null \
        GIT_AUTHOR_NAME="Harness" GIT_AUTHOR_EMAIL="harness@example.invalid" \
        GIT_COMMITTER_NAME="Harness" GIT_COMMITTER_EMAIL="harness@example.invalid" \
        timeout --kill-after=10s "${limit}s" \
        dotnet "$dll" $harness_args >"$log" 2>&1
    status=$?
    elapsed=$((SECONDS - started))

    if [[ $status -eq 0 ]]; then
        # The PASS line is the harness's own summary; echoing it keeps the count
        # visible, which is what makes a suite that silently stopped asserting
        # anything noticeable.
        summary="$(grep -m1 '^PASS' "$log" || echo "exited 0")"
        printf '==> %-20s ok   %3ds  %s\n' "$name" "$elapsed" "$summary"
    elif [[ $status -eq 124 || $status -eq 137 ]]; then
        printf '==> %-20s TIMEOUT after %ss\n' "$name" "$limit"
        FAILED+=("$name")
    else
        printf '==> %-20s FAILED (exit %d, %ds)\n' "$name" "$status" "$elapsed"
        FAILED+=("$name")
    fi
    TIMINGS+=("$name:$elapsed")
done

echo
if [[ ${#FAILED[@]} -eq 0 ]]; then
    echo "ALL GREEN: ${#HARNESSES[@]} harnesses (${TIMINGS[*]})"
    rm -rf "$SCRATCH"
    exit 0
fi

for name in "${FAILED[@]}"; do
    echo "----- $name -----"
    cat "$SCRATCH/$name.log" 2>/dev/null || echo "(no output)"
    # A harness killed by the timeout above has usually printed nothing at all, and
    # an empty block here reads like a missing log rather than the actual finding:
    # that the suite hung. Say which it is — but only when the harness really ran and
    # said nothing, which is not the same as never having been started.
    if [[ -f "$SCRATCH/$name.log" && ! -s "$SCRATCH/$name.log" ]]; then
        echo "(the log is empty: $name produced no output before it was stopped — it hung rather than failed)"
    fi
    echo
done
echo "FAILED: ${FAILED[*]}"
echo "Scratch directories kept for inspection: $SCRATCH"
exit 1
