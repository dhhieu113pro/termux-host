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
NGROK_URL="https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-linux-arm64.tgz"

export DEBIAN_FRONTEND=noninteractive
export APT_LISTCHANGES_FRONTEND=none
export NEEDRESTART_MODE=a
export UCF_FORCE_CONFFOLD=1
APT_OPTIONS=(
  -o Dpkg::Options::=--force-confold
  -o Dpkg::Options::=--force-confdef
)

apt_noninteractive() {
  set +o pipefail
  yes '' | apt-get "${APT_OPTIONS[@]}" "$@"
  local apt_status=${PIPESTATUS[1]}
  set -o pipefail
  return "$apt_status"
}

echo "==> Updating Termux packages"
apt_noninteractive update
apt_noninteractive -y upgrade

echo "==> Installing dependencies"
apt_noninteractive install -y \
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
  zip \
  tar

echo "==> Installing ngrok"
ARCH="$(uname -m)"
case "$ARCH" in
  aarch64|arm64)
    NGROK_TMP="$(mktemp -d)"
    trap 'rm -rf "$NGROK_TMP"' EXIT
    curl -fL --retry 3 -o "$NGROK_TMP/ngrok.tgz" "$NGROK_URL"
    tar -xzf "$NGROK_TMP/ngrok.tgz" -C "$NGROK_TMP"
    install -m 755 "$NGROK_TMP/ngrok" "$PREFIX/bin/ngrok"
    ngrok version
    rm -rf "$NGROK_TMP"
    trap - EXIT
    ;;
  *)
    echo "Unsupported architecture for bundled ngrok installer: $ARCH" >&2
    exit 1
    ;;
esac

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

# Stop the old host before switching the current release.
sv down termux-host >/dev/null 2>&1 || true
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

# Auto-start whenever the termux-services supervisor starts.
sv-enable termux-host || true
sv up termux-host || true

# Termux:Boot support. The companion Termux:Boot app will execute this after
# an Android reboot. It starts the termux-services supervisor; runit then
# restores TermuxHost, auto-start apps, and the previously configured ngrok service.
mkdir -p "$HOME/.termux/boot"
cat > "$HOME/.termux/boot/termux-host-services" <<EOF
#!/data/data/com.termux/files/usr/bin/sh
export PREFIX="$PREFIX"
export PATH="$PREFIX/bin:/system/bin:/system/xbin"
if [ -f "$PREFIX/etc/profile.d/start-services.sh" ]; then
  . "$PREFIX/etc/profile.d/start-services.sh"
fi
sleep 2
sv up termux-host >/dev/null 2>&1 || true
EOF
chmod +x "$HOME/.termux/boot/termux-host-services"

IP="$(ip route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i=="src"){print $(i+1); exit}}' || true)"
if [ -z "$IP" ]; then
  IP="$(ip -4 addr 2>/dev/null | awk '/inet / && $2 !~ /^127\./ {split($2,a,"/"); print a[1]; exit}' || true)"
fi

echo
echo "TermuxHost $VERSION installed."
echo "Service: sv status termux-host"
echo "Local:   http://127.0.0.1:$PORT"
if [ -n "$IP" ]; then
  echo "LAN:     http://$IP:$PORT"
fi
echo "ngrok:   $(ngrok version 2>/dev/null || echo installed)"
echo
echo "Auto-start: TermuxHost and all apps with AutoStart enabled are restored by runit."
echo "ngrok: A tunnel that has been started once is restored automatically."
echo "Android reboot: install/open the Termux:Boot companion app once; the boot helper is already created."
echo
echo "If this is the first time installing termux-services, restart the Termux shell once so runit is initialized automatically."
