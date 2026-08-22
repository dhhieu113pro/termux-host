#!/data/data/com.termux/files/usr/bin/bash

set -euo pipefail

REPO="dhhieu113pro/termux-host"
APP_ROOT="$HOME/termux-host"
RELEASES_DIR="$APP_ROOT/releases"
CURRENT_LINK="$APP_ROOT/current"
SERVICE_DIR="$PREFIX/var/service/termux-host"
PORT="${TERMUX_HOST_PORT:-5050}"
PACKAGE_FILE="${TERMUX_HOST_PACKAGE_FILE:-}"
VERSION="${TERMUX_HOST_VERSION:-}"
ASSET_NAME="termux-host-aarch64.zip"

echo "==> Updating Termux packages"
pkg update -y
pkg upgrade -y

echo "==> Installing dependencies"
pkg install -y \
  dotnet-sdk-10.0 \
  git \
  gh \
  termux-services \
  openssh \
  curl \
  wget \
  jq \
  procps \
  net-tools \
  inetutils \
  tmux \
  htop \
  unzip \
  zip

mkdir -p "$RELEASES_DIR"

if [ -z "$PACKAGE_FILE" ]; then
  echo "==> Checking latest GitHub release"
  RELEASE_JSON="$(curl -fsSL -H 'Accept: application/vnd.github+json' "https://api.github.com/repos/$REPO/releases/latest")"
  VERSION="${VERSION:-$(printf '%s' "$RELEASE_JSON" | jq -r '.tag_name')}"
  DOWNLOAD_URL="$(printf '%s' "$RELEASE_JSON" | jq -r --arg name "$ASSET_NAME" '.assets[] | select(.name == $name) | .browser_download_url' | head -n1)"

  if [ -z "$VERSION" ] || [ "$VERSION" = "null" ]; then
    echo "Unable to determine latest TermuxHost release." >&2
    exit 1
  fi

  if [ -z "$DOWNLOAD_URL" ] || [ "$DOWNLOAD_URL" = "null" ]; then
    echo "Release $VERSION does not contain $ASSET_NAME." >&2
    exit 1
  fi

  PACKAGE_FILE="$APP_ROOT/$ASSET_NAME"
  echo "==> Downloading TermuxHost $VERSION"
  curl -fL --retry 3 -o "$PACKAGE_FILE" "$DOWNLOAD_URL"
else
  VERSION="${VERSION:-ci}"
  echo "==> Installing local package $PACKAGE_FILE ($VERSION)"
fi

RELEASE_DIR="$RELEASES_DIR/$VERSION"
rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"
unzip -q "$PACKAGE_FILE" -d "$RELEASE_DIR"

test -f "$RELEASE_DIR/TermuxHost.dll"
printf '%s\n' "$VERSION" > "$RELEASE_DIR/VERSION"

ln -sfn "$RELEASE_DIR" "$CURRENT_LINK"

echo "==> Configuring runit service"
mkdir -p "$SERVICE_DIR/log"

cat > "$SERVICE_DIR/run" <<EOF
#!/data/data/com.termux/files/usr/bin/sh
export HOME="$HOME"
export PREFIX="$PREFIX"
export PATH="$PREFIX/bin:/system/bin:/system/xbin"
export ASPNETCORE_ENVIRONMENT="Production"
export ASPNETCORE_URLS="http://0.0.0.0:$PORT"
cd "$CURRENT_LINK"
exec dotnet TermuxHost.dll 2>&1
EOF

cat > "$SERVICE_DIR/log/run" <<EOF
#!/data/data/com.termux/files/usr/bin/sh
mkdir -p "$APP_ROOT/logs"
exec svlogd -tt "$APP_ROOT/logs"
EOF

chmod +x "$SERVICE_DIR/run" "$SERVICE_DIR/log/run"

sv-enable termux-host || true
sv up termux-host || true

IP="$(ip -4 addr show wlan0 2>/dev/null | awk '/inet / {print $2}' | cut -d/ -f1 | head -n1 || true)"

echo
echo "TermuxHost $VERSION installed."
echo "Service: sv status termux-host"
echo "Local:   http://127.0.0.1:$PORT"
if [ -n "$IP" ]; then
  echo "LAN:     http://$IP:$PORT"
fi
echo
echo "If this is the first time installing termux-services, restart the Termux shell once so runit is initialized automatically."
