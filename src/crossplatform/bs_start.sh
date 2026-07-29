#!/bin/bash
# Launches the port on the private display :217 against the bisect test repo.
export PATH="$HOME/.dotnet:$PATH"
export DISPLAY=:217
W=/home/dario/git_ext_mod/.claude/worktrees/agent-a6cc50c817c8564c9/src/crossplatform
S=/tmp/loop-verify/bs
mkdir -p "$S/xdg"
export XDG_CONFIG_HOME="$S/xdg"
DLL="$(find "$W/bin" -name GitExtensions.Avalonia.dll | head -1)"
echo "DLL=$DLL"
cd /tmp/bs1repo
nohup dotnet "$DLL" /tmp/bs1repo >"$S/gui.log" 2>&1 &
echo $! > "$S/app.pid"
disown
echo "pid=$(cat $S/app.pid)"
