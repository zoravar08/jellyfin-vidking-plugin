#!/bin/bash
# Installer for Jellyfin VidKing Link Monitor (companion tool)
# Installs to /opt/jellyfin-vidking-monitor and sets up cron + systemd timer

set -e
set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
INSTALL_DIR="/opt/jellyfin-vidking-monitor"
SYSTEMD_DIR="/etc/systemd/system"
CRON_FILE="/etc/cron.d/jellyfin-vidking-monitor"

echo "=== Jellyfin VidKing Link Monitor Installer ==="
echo "Source: $SCRIPT_DIR"
echo "Install: $INSTALL_DIR"

# Create install dir
sudo mkdir -p "$INSTALL_DIR"

# Copy monitor script
sudo cp "$SCRIPT_DIR/vking_link_monitor.py" "$INSTALL_DIR/"
sudo chmod +x "$INSTALL_DIR/vking_link_monitor.py"

# Copy config template
sudo cp "$SCRIPT_DIR/monitor_config.json" "$INSTALL_DIR/monitor_config.json"
sudo chmod 600 "$INSTALL_DIR/monitor_config.json"

# Create systemd service
sudo tee "$SYSTEMD_DIR/jellyfin-vidking-monitor.service" > /dev/null << 'EOF'
[Unit]
Description=Jellyfin VidKing Link Monitor
After=network.target

[Service]
Type=oneshot
ExecStart=/usr/bin/python3 /opt/jellyfin-vidking-monitor/vking_link_monitor.py
WorkingDirectory=/opt/jellyfin-vidking-monitor
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

# Create systemd timer (every 6 hours)
sudo tee "$SYSTEMD_DIR/jellyfin-vidking-monitor.timer" > /dev/null << 'EOF'
[Unit]
Description=Run Jellyfin VidKing Link Monitor every 6 hours
Requires=jellyfin-vidking-monitor.service

[Timer]
OnCalendar=*-*-* 00/6:00:00
Persistent=true

[Install]
WantedBy=timers.target
EOF

# Enable and start timer
sudo systemctl daemon-reload
sudo systemctl enable jellyfin-vidking-monitor.timer
sudo systemctl start jellyfin-vidking-monitor.timer

echo ""
echo "=== Installation complete ==="
echo "Monitor: $INSTALL_DIR/vking_link_monitor.py"
echo "Config:  $INSTALL_DIR/monitor_config.json"
echo "Timer:   jellyfin-vidking-monitor.timer (every 6 hours)"
echo ""
echo "Next steps:"
echo "  1. Edit config: sudo nano $INSTALL_DIR/monitor_config.json"
echo "     Set bot_token and chat_id for Telegram alerts"
echo "  2. Test run:   sudo python3 $INSTALL_DIR/vking_link_monitor.py"
echo "  3. View logs:  journalctl -u jellyfin-vidking-monitor.service"
echo ""
echo "To disable:"
echo "  sudo systemctl disable jellyfin-vidking-monitor.timer"
echo "  sudo systemctl stop jellyfin-vidking-monitor.timer"
