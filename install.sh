#!/data/data/com.termux/files/usr/bin/bash

set -euo pipefail

REPO_URL="${TERMUX_HOST_REPO_URL:-https://github.com/dhhieu113pro/termux-host.git}"
SOURCE_DIR="${TERMUX_HOST_SOURCE_DIR:-}"
APP_ROOT="$HOME/termux-host"
PUBLISH_DIR="$APP_ROOT/publish"
SERVICE_DIR="$PREFIX/var/service/termux-host"
PORT="${TERMUX_HOST_PORT:-5050}"

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

echo "==> Preparing source"
if [ -n "$SOURCE_DIR" ]; then
  if [ ! -d "$SOURCE_DIR" ]; then
    echo "Source directory does not exist: $SOURCE_DIR" >&2
    exit 1
  fi

  rm -rf "$APP_ROOT"
  mkdir -p "$APP_ROOT"
  cp -a "$SOURCE_DIR"/. "$APP_ROOT"/
elif [ -d "$APP_ROOT/.git" ]; then
  git -C "$APP_ROOT" pull --ff-only
else
  rm -rf "$APP_ROOT"
  git clone "$REPO_URL" "$APP_ROOT"
fi

echo "==> Publishing TermuxHost"
cd "$APP_ROOT"
dotnet restore
dotnet publish -c Release -o "$PUBLISH_DIR"

echo "==> Configuring runit service"
mkdir -p "$SERVICE_DIR/log"

cat > "$SERVICE_DIR/run" <<EOF
#!/data/data/com.termux/files/usr/bin/sh
export HOME="$HOME"
export PREFIX="$PREFIX"
export PATH="$PREFIX/bin:/system/bin:/system/xbin"
export ASPNETCORE_ENVIRONMENT="Production"
export ASPNETCORE_URLS="http://0.0.0.0:$PORT"
cd "$PUBLISH_DIR"
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
echo "TermuxHost installed."
echo "Service: sv status termux-host"
echo "Local:   http://127.0.0.1:$PORT"
if [ -n "$IP" ]; then
  echo "LAN:     http://$IP:$PORT"
fi
echo
echo "If this is the first time installing termux-services, restart the Termux shell once so runit is initialized automatically."
