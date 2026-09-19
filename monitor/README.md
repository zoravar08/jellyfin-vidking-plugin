# Jellyfin VidKing Link Monitor

Standalone companion tool for the
[Jellyfin VidKing Integration plugin](https://github.com/USER/jellyfin-vidking-plugin).
Probes all `.vking` items' ShortcutPath URLs for validity, tracks URL changes,
and sends Telegram alerts on failure.

This is **not** the plugin itself — it's a separate monitoring tool that works
alongside the installed plugin.

## What it does

- Reads all `.vking` items from Jellyfin's `jellyfin.db` (needs read access to
  `/var/lib/jellyfin/data/jellyfin.db`)
- Sends HTTP HEAD requests with `Referer: https://1embed.cc/` to each
  ShortcutPath URL
- Flags URLs that return non-MP4 content (proxy-status page, HTML, errors)
- Sends a Telegram alert on failure (configurable)
- Tracks URL changes over time in a local SQLite database (`monitor_state.db`)
- Runs every 6 hours via systemd timer (or manually)

## Requirements

- Python 3 (stdlib only — no third-party packages needed)
- Read access to Jellyfin's `jellyfin.db`
- Optional: Telegram bot token + chat ID for alerts

## Installation

### Automatic (install script)

```bash
curl -sL https://raw.githubusercontent.com/USER/jellyfin-vidking-plugin/main/monitor/install.sh | sudo bash
```

Then edit the config:
```bash
sudo nano /opt/jellyfin-vidking-monitor/monitor_config.json
```

### Manual

```bash
sudo mkdir -p /opt/jellyfin-vidking-monitor
sudo cp vking_link_monitor.py /opt/jellyfin-vidking-monitor/
sudo cp monitor_config.json /opt/jellyfin-vidking-monitor/
sudo chmod +x /opt/jellyfin-vidking-monitor/vking_link_monitor.py

# Create systemd service + timer
sudo cp jellyfin-vidking-monitor.service /etc/systemd/system/
sudo cp jellyfin-vidking-monitor.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now jellyfin-vidking-monitor.timer
```

## Configuration

Edit `monitor_config.json`:

```json
{
  "bot_token": "5347967691:YOUR_REAL_TOKEN_HERE",
  "chat_id": "6411159860",
  "probe_timeout": 15,
  "enable_telegram": true
}
```

- `bot_token` — your Telegram bot token from @BotFather
- `chat_id` — the Telegram chat ID to send alerts to
- `probe_timeout` — seconds to wait for each URL probe (default 15)
- `enable_telegram` — set to `false` to disable Telegram alerts (monitor still logs)

## Running manually

```bash
sudo python3 /opt/jellyfin-vidking-monitor/vking_link_monitor.py
```

## Logs

```bash
# Via systemd timer
journalctl -u jellyfin-vidking-monitor.service

# Direct log file (when run manually)
tail -f /opt/jellyfin-vidking-monitor/monitor.log
```

## URL change tracking

The monitor tracks how many times each URL has changed in `monitor_state.db`.
Query it:

```bash
sqlite3 /opt/jellyfin-vidking-monitor/monitor_state.db \
  "SELECT item_name, times_changed, last_status FROM url_history ORDER BY times_changed DESC;"
```

## Important caveat

1Embed Cloudflare Worker URLs are **session-bound** and **domain-rotated**.
If a URL in Jellyfin's ShortcutPath starts returning `1Embed Stream Proxy Active`
(proxy-status page) instead of `video/mp4`, the session expired. Fix by
re-extracting the URL via the plugin (toggle Stream Extraction + library rescan,
or call `/VidKing/Extract/{itemId}`).

Rate-limited URLs (HTTP 429) are **not** treated as failures — they're logged
but not alerted, since Jellyfin server-side ffmpeg can still play them.
