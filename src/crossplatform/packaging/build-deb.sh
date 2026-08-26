#!/usr/bin/env bash
# Build a reproducible Debian .deb for the Linux/Avalonia port of Git Extensions.
#
# Produces a self-contained package (no .NET SDK/runtime required on the target
# machine) that installs to /opt/gitnext with a /usr/bin/gitnext
# launcher, a .desktop entry and an application icon.
#
# Usage:
#   ./build-deb.sh
#
# Output:
#   packaging/out/gitnext_<version>_amd64.deb
set -euo pipefail

# --- Locations ---------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"           # src/crossplatform
REPO_ROOT="$(cd "$CP_DIR/../.." && pwd)"         # repo root
PROJ="$CP_DIR/App/GitExtensions.Avalonia.csproj"

OUT_DIR="$SCRIPT_DIR/out"
PUBLISH_DIR="$OUT_DIR/publish"
STAGE_DIR="$OUT_DIR/stage"

APP_BINARY="GitNext"                             # AssemblyName -> native launcher
RID="linux-x64"
# The product mark, exported from packaging/../App/Assets/Icons/gitNext.svg into
# one PNG per hicolor size. Installing every size (not just 256) is what lets a
# panel, a dock and an alt-tab switcher each pick the one they need instead of
# downscaling the big one themselves.
ICON_DIR="$SCRIPT_DIR/icons"
ICON_SIZES="16 24 32 48 64 128 256 512"

# --- Version -----------------------------------------------------------------
if [[ -f "$SCRIPT_DIR/VERSION" ]]; then
    VERSION="$(tr -d '[:space:]' < "$SCRIPT_DIR/VERSION")"
else
    VERSION="5.0.0-linux1"
fi
DEB_NAME="gitnext_${VERSION}_amd64.deb"

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
for size in $ICON_SIZES; do
    if [[ ! -f "$ICON_DIR/gitnext-$size.png" ]]; then
        echo "error: icon not found at $ICON_DIR/gitnext-$size.png" >&2
        exit 1
    fi
done

echo "==> Building gitNext ${VERSION} (${RID})"

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

# The XLIFF catalogues are pulled in by the csproj (<None … Link="Translation\…">)
# and land next to the assemblies, which is exactly where the core Translator
# looks for them. "cp -a $PUBLISH_DIR/." below then carries them into the package.
# Fail loudly if they went missing: the app would silently be English-only.
if [[ ! -d "$PUBLISH_DIR/Translation" ]] || ! compgen -G "$PUBLISH_DIR/Translation/*.xlf" >/dev/null; then
    echo "error: no Translation/*.xlf in the publish output — the UI would be English-only" >&2
    exit 1
fi
echo "==> Translations: $(find "$PUBLISH_DIR/Translation" -name '*.xlf' | wc -l) .xlf files"

# --- 2. Stage the Debian tree ------------------------------------------------
echo "==> Staging Debian tree"
INSTALL_ROOT="/opt/gitnext"
mkdir -p "$STAGE_DIR/DEBIAN"
mkdir -p "$STAGE_DIR$INSTALL_ROOT"
mkdir -p "$STAGE_DIR/usr/bin"
mkdir -p "$STAGE_DIR/usr/share/applications"
for size in $ICON_SIZES; do
    mkdir -p "$STAGE_DIR/usr/share/icons/hicolor/${size}x${size}/apps"
done

# Payload
cp -a "$PUBLISH_DIR/." "$STAGE_DIR$INSTALL_ROOT/"
chmod +x "$STAGE_DIR$INSTALL_ROOT/$APP_BINARY"

# Installed size (KiB) for control metadata
INSTALLED_SIZE="$(du -sk "$STAGE_DIR$INSTALL_ROOT" | cut -f1)"

# Launcher wrapper
cat > "$STAGE_DIR/usr/bin/gitnext" <<EOF
#!/bin/sh
exec $INSTALL_ROOT/$APP_BINARY "\$@"
EOF
chmod 0755 "$STAGE_DIR/usr/bin/gitnext"

# Desktop entry + icon
cp "$SCRIPT_DIR/gitnext.desktop" "$STAGE_DIR/usr/share/applications/gitnext.desktop"
for size in $ICON_SIZES; do
    cp "$ICON_DIR/gitnext-$size.png" "$STAGE_DIR/usr/share/icons/hicolor/${size}x${size}/apps/gitnext.png"
done

# DEBIAN/control
cat > "$STAGE_DIR/DEBIAN/control" <<EOF
Package: gitnext
Version: ${VERSION}
Architecture: amd64
Section: vcs
Priority: optional
Depends: git
Installed-Size: ${INSTALLED_SIZE}
Maintainer: gitNext <noreply@example.invalid>
Description: gitNext - graphical user interface for Git
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
