#!/usr/bin/env bash
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this script with sudo."
    exit 1
fi

APP_ROOT="${APP_ROOT:-/opt/bittorrent}"
APP_USER="${APP_USER:-bittorrent}"
BINARY="${BINARY:-$APP_ROOT/BitTorrent}"
DATA_DIR="${DATA_DIR:-$APP_ROOT/data}"
TORRENT_FILE="${TORRENT_FILE:-$APP_ROOT/payload.bin.torrent}"
PEER_PORT="${PEER_PORT:-5001}"
TRACKER_PORT="${TRACKER_PORT:-6969}"
PUBLIC_IP="${PUBLIC_IP:-}"

if [ -z "$PUBLIC_IP" ]; then
    echo "PUBLIC_IP is required, for example: sudo PUBLIC_IP=203.0.113.10 bash deploy/install-ubuntu.sh"
    exit 1
fi

if [ ! -x "$BINARY" ]; then
    echo "Missing executable: $BINARY"
    exit 1
fi

if [ ! -f "$TORRENT_FILE" ]; then
    echo "Missing torrent file: $TORRENT_FILE"
    exit 1
fi

if ! id -u "$APP_USER" >/dev/null 2>&1; then
    useradd --system --home "$APP_ROOT" --shell /usr/sbin/nologin "$APP_USER"
fi

mkdir -p "$APP_ROOT" "$DATA_DIR"
chown -R "$APP_USER:$APP_USER" "$APP_ROOT"

cat >/etc/systemd/system/bittorrent-tracker.service <<UNIT
[Unit]
Description=BitTorrent demo HTTP tracker
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=$APP_ROOT
ExecStart=$BINARY tracker http://*:$TRACKER_PORT/announce/ $PUBLIC_IP
Restart=always
RestartSec=5
User=$APP_USER

[Install]
WantedBy=multi-user.target
UNIT

cat >/etc/systemd/system/bittorrent-client.service <<UNIT
[Unit]
Description=BitTorrent demo seeder
After=network-online.target bittorrent-tracker.service
Wants=network-online.target

[Service]
WorkingDirectory=$APP_ROOT
ExecStart=$BINARY client $PEER_PORT $TORRENT_FILE $DATA_DIR
Restart=always
RestartSec=5
User=$APP_USER

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now bittorrent-tracker.service
systemctl enable --now bittorrent-client.service

if command -v ufw >/dev/null 2>&1; then
    ufw allow "$TRACKER_PORT/tcp" >/dev/null || true
    ufw allow "$PEER_PORT/tcp" >/dev/null || true
fi

echo "Installed services:"
echo "  bittorrent-tracker.service on TCP $TRACKER_PORT"
echo "  bittorrent-client.service on TCP $PEER_PORT"
echo "Open both ports in your cloud firewall/security list too."
