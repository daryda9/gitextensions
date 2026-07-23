#!/usr/bin/env bash
# Build a reproducible Debian .deb for the Linux/Avalonia port of Git Extensions.
#
# Produces a self-contained package (no .NET SDK/runtime required on the target
# machine) that installs to /opt/gitextensions with a /usr/bin/gitextensions
# launcher, a .desktop entry and an application icon.
#
# Usage:
#   ./build-deb.sh
#
# Output:
#   packaging/out/gitextensions_<version>_amd64.deb
set -euo pipefail

# --- Locations ---------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"           # src/crossplatform
REPO_ROOT="$(cd "$CP_DIR/../.." && pwd)"         # repo root
PROJ="$CP_DIR/App/GitExtensions.Avalonia.csproj"

OUT_DIR="$SCRIPT_DIR/out"
PUBLISH_DIR="$OUT_DIR/publish"
STAGE_DIR="$OUT_DIR/stage"

APP_BINARY="GitExtensions.Avalonia"              # AssemblyName -> native launcher
RID="linux-x64"
ICON_SRC="$REPO_ROOT/setup/assets/Logo/git-extensions-logo-256px.png"

# --- Version -----------------------------------------------------------------
if [[ -f "$SCRIPT_DIR/VERSION" ]]; then
    VERSION="$(tr -d '[:space:]' < "$SCRIPT_DIR/VERSION")"
else
    VERSION="5.0.0-linux1"
fi
DEB_NAME="gitextensions_${VERSION}_amd64.deb"

# --- .NET on PATH ------------------------------------------------------------
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: dotnet not found. Expected .NET 10 SDK at $DOTNET_ROOT" >&2
    exit 1
fi
if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "error: dpkg-deb not found. Install with: sudo apt install dpkg-dev" >&2
    exit 1
fi
if [[ ! -f "$ICON_SRC" ]]; then
    echo "error: icon not found at $ICON_SRC" >&2
    exit 1
fi

echo "==> Building gitextensions ${VERSION} (${RID})"

# --- 1. Clean + self-contained publish --------------------------------------
rm -rf "$STAGE_DIR" "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

echo "==> dotnet publish (self-contained)"
dotnet publish "$PROJ" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -o "$PUBLISH_DIR"

if [[ ! -f "$PUBLISH_DIR/$APP_BINARY" ]]; then
    echo "error: expected published binary $PUBLISH_DIR/$APP_BINARY not found" >&2
    echo "       publish output:" >&2
    ls -la "$PUBLISH_DIR" >&2
    exit 1
fi

# --- 2. Stage the Debian tree ------------------------------------------------
echo "==> Staging Debian tree"
INSTALL_ROOT="/opt/gitextensions"
mkdir -p "$STAGE_DIR/DEBIAN"
mkdir -p "$STAGE_DIR$INSTALL_ROOT"
mkdir -p "$STAGE_DIR/usr/bin"
mkdir -p "$STAGE_DIR/usr/share/applications"
mkdir -p "$STAGE_DIR/usr/share/icons/hicolor/256x256/apps"

# Payload
cp -a "$PUBLISH_DIR/." "$STAGE_DIR$INSTALL_ROOT/"
chmod +x "$STAGE_DIR$INSTALL_ROOT/$APP_BINARY"

# Installed size (KiB) for control metadata
INSTALLED_SIZE="$(du -sk "$STAGE_DIR$INSTALL_ROOT" | cut -f1)"

# Launcher wrapper
cat > "$STAGE_DIR/usr/bin/gitextensions" <<EOF
#!/bin/sh
exec $INSTALL_ROOT/$APP_BINARY "\$@"
EOF
chmod 0755 "$STAGE_DIR/usr/bin/gitextensions"

# Desktop entry + icon
cp "$SCRIPT_DIR/gitextensions.desktop" "$STAGE_DIR/usr/share/applications/gitextensions.desktop"
cp "$ICON_SRC" "$STAGE_DIR/usr/share/icons/hicolor/256x256/apps/gitextensions.png"

# DEBIAN/control
cat > "$STAGE_DIR/DEBIAN/control" <<EOF
Package: gitextensions
Version: ${VERSION}
Architecture: amd64
Section: vcs
Priority: optional
Depends: git
Installed-Size: ${INSTALLED_SIZE}
Maintainer: Git Extensions Linux Port <noreply@gitextensions.github.io>
Description: Graphical user interface for Git (Linux/Avalonia port)
 Git Extensions is a standalone UI tool for managing Git repositories.
 This is the cross-platform Linux port built on Avalonia and .NET,
 shipped self-contained so no separate .NET runtime is required.
EOF

# DEBIAN/postinst
cat > "$STAGE_DIR/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi

exit 0
EOF
chmod 0755 "$STAGE_DIR/DEBIAN/postinst"

# Normalise permissions for a well-formed package
find "$STAGE_DIR" -type d -exec chmod 0755 {} +

# --- 3. Build the .deb -------------------------------------------------------
echo "==> dpkg-deb --build"
dpkg-deb --root-owner-group --build "$STAGE_DIR" "$OUT_DIR/$DEB_NAME"

echo ""
echo "==> Done: $OUT_DIR/$DEB_NAME"
ls -la "$OUT_DIR/$DEB_NAME"
