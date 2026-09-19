#!/usr/bin/env python3
"""
VKing Link Monitor — probes all .vking items' ShortcutPath URLs for validity,
tracks URL changes, and sends Telegram alerts on failure.

Runs as a cron job (default: every 6 hours). Also usable as a one-shot check.

Configuration:
  - Reads bot token + chat_id from /home/ubuntu/jellyfin-vidking-plugin/monitor_config.json
  - Reads Jellyfin DB from /var/lib/jellyfin/data/jellyfin.db
  - State stored in /home/ubuntu/jellyfin-vidking-plugin/monitor_state.db
  - Logs to /home/ubuntu/jellyfin-vidking-plugin/monitor.log
"""
import sys
import json
import sqlite3
import logging
import datetime
import urllib.request
import urllib.error
import ssl
from pathlib import Path

# ── Paths ──────────────────────────────────────────────────────────
BASE_DIR = Path("/home/ubuntu/jellyfin-vidking-plugin")
JELLYFIN_DB = Path("/var/lib/jellyfin/data/jellyfin.db")
CONFIG_FILE = BASE_DIR / "monitor_config.json"
STATE_DB = BASE_DIR / "monitor_state.db"
LOG_FILE = BASE_DIR / "monitor.log"

# ── Logging ────────────────────────────────────────────────────────
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.FileHandler(LOG_FILE),
        logging.StreamHandler(sys.stdout),
    ],
)
logger = logging.getLogger("VKingLinkMonitor")

# ── Config ─────────────────────────────────────────────────────────
def load_config():
    if not CONFIG_FILE.exists():
        logger.warning(f"Config file {CONFIG_FILE} not found — Telegram alerts disabled")
        return {"bot_token": None, "chat_id": None, "probe_timeout": 15, "enable_telegram": False}
    try:
        with open(CONFIG_FILE) as f:
            cfg = json.load(f)
        return {
            "bot_token": cfg.get("bot_token"),
            "chat_id": cfg.get("chat_id"),
            "probe_timeout": cfg.get("probe_timeout", 15),
            "enable_telegram": cfg.get("enable_telegram", False) and bool(cfg.get("bot_token")),
        }
    except Exception as e:
        logger.error(f"Failed to load config: {e}")
        return {"bot_token": None, "chat_id": None, "probe_timeout": 15, "enable_telegram": False}

# ── Telegram ───────────────────────────────────────────────────────
def send_telegram(message: str, bot_token: str, chat_id: str) -> bool:
    if not bot_token or not chat_id:
        return False
    url = f"https://api.telegram.org/bot{bot_token}/sendMessage"
    data = json.dumps({"chat_id": chat_id, "text": message, "parse_mode": "HTML"}).encode()
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            result = json.loads(resp.read())
            if result.get("ok"):
                logger.info(f"Telegram alert sent to {chat_id}")
                return True
            else:
                logger.error(f"Telegram API error: {result.get('description')}")
                return False
    except Exception as e:
        logger.error(f"Telegram send failed: {e}")
        return False

# ── URL Probing ────────────────────────────────────────────────────
def probe_url(url: str, timeout: int = 15) -> dict:
    """
    Probe a ShortcutPath URL to verify it's still a valid MP4.
    Sends HTTP HEAD with Referer header (as ffmpeg uses).
    Returns: valid (True/False/None for rate-limited), status_code, content_type, error, size
    """
    result = {
        "valid": False,
        "status_code": 0,
        "content_type": "",
        "error": "",
        "size": 0,
    }
    if not url or not url.startswith("http"):
        result["error"] = "Invalid URL (not HTTP)"
        return result

    req = urllib.request.Request(url, method="HEAD")
    req.add_header("Referer", "https://1embed.cc/")
    req.add_header("User-Agent", "Jellyfin/10.9.0")
    req.add_header("Accept", "*/*")
    req.add_header("Range", "bytes=0-0")

    try:
        ctx = ssl.create_default_context()
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as resp:
            result["status_code"] = resp.status
            result["content_type"] = resp.headers.get("Content-Type", "")
            result["size"] = int(resp.headers.get("Content-Length", 0) or 0)
            if resp.status in (200, 206) and "video/mp4" in result["content_type"]:
                result["valid"] = True
            elif resp.status == 200 and result["size"] > 0:
                result["error"] = f"Unexpected content-type: {result['content_type']} (size={result['size']})"
            else:
                result["error"] = f"Status {resp.status}, content-type: {result['content_type']}"
    except urllib.error.HTTPError as e:
        result["status_code"] = e.code
        if e.code == 429:
            result["error"] = "Rate limited (429) — worker throttling; will retry next run"
            result["content_type"] = "text/plain"
            result["valid"] = None  # Rate-limited, not a failure
        else:
            result["error"] = f"HTTP {e.code}: {e.reason}"
            try:
                body = e.read(200).decode("utf-8", errors="replace")
                if "1Embed Stream Proxy Active" in body:
                    result["error"] = "1Embed Stream Proxy Active — proxy-status page (session expired / client-type gated)"
                    result["content_type"] = "text/plain"
            except:
                pass
    except urllib.error.URLError as e:
        result["error"] = f"Connection error: {e.reason}"
    except Exception as e:
        result["error"] = str(e)
    return result

# ── State Database ─────────────────────────────────────────────────
def init_state_db():
    conn = sqlite3.connect(STATE_DB)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS url_history (
            item_id TEXT PRIMARY KEY,
            item_name TEXT,
            current_url TEXT,
            previous_url TEXT,
            last_probed TEXT,
            last_status TEXT,
            times_changed INTEGER DEFAULT 0,
            first_seen TEXT,
            last_changed TEXT
        )
    """)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS probe_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            item_id TEXT,
            item_name TEXT,
            url TEXT,
            valid INTEGER,
            status_code INTEGER,
            content_type TEXT,
            error TEXT,
            probed_at TEXT
        )
    """)
    conn.commit()
    conn.close()

def update_state(item_id: str, item_name: str, url: str, probe_result: dict):
    conn = sqlite3.connect(STATE_DB)
    cursor = conn.cursor()
    cursor.execute(
        "SELECT current_url, previous_url, times_changed, first_seen, last_changed FROM url_history WHERE item_id = ?",
        (item_id,)
    )
    row = cursor.fetchone()
    now_str = datetime.datetime.now().isoformat()

    if row:
        prev_url = row[0]
        times_changed = row[2] or 0
    else:
        prev_url = None
        times_changed = 0

    url_changed = (prev_url != url) if prev_url else True
    if url_changed:
        times_changed += 1
        logger.info(f"  URL changed for {item_name}: {'(new)' if not prev_url else prev_url[:60]} → {url[:60]}")

    was_valid = probe_result["valid"]
    if was_valid is True:
        status_text = f"OK ({probe_result['status_code']}, {probe_result['content_type']})"
    elif was_valid is None:
        status_text = f"RATELIMITED ({probe_result['error']})"
    else:
        status_text = f"FAIL: {probe_result['error']}"

    cursor.execute("""
        INSERT OR REPLACE INTO url_history
        (item_id, item_name, current_url, previous_url, last_probed, last_status, times_changed, first_seen, last_changed)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (item_id, item_name, url, prev_url, now_str, status_text,
          times_changed,
          row[3] if row else now_str,
          now_str if url_changed else (row[4] if row else None)))

    cursor.execute("""
        INSERT INTO probe_log (item_id, item_name, url, valid, status_code, content_type, error, probed_at)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    """, (item_id, item_name, url,
          1 if was_valid else (0 if was_valid is False else -1),
          probe_result["status_code"],
          probe_result["content_type"],
          probe_result["error"],
          now_str))

    conn.commit()
    conn.close()
    return url_changed, times_changed

# ── Jellyfin DB Query ──────────────────────────────────────────────
def get_vking_items():
    conn = sqlite3.connect(JELLYFIN_DB)
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()
    cursor.execute("SELECT Id, Name, Data FROM BaseItems WHERE Path LIKE '%.vking'")
    items = []
    for row in cursor.fetchall():
        try:
            d = json.loads(row["Data"])
            shortcut_path = d.get("ShortcutPath", "")
        except:
            shortcut_path = ""
        items.append({"id": row["Id"], "name": row["Name"], "url": shortcut_path})
    conn.close()
    return items

# ── Main ───────────────────────────────────────────────────────────
def main():
    logger.info("=" * 60)
    logger.info("VKing Link Monitor starting")

    cfg = load_config()
    logger.info(f"Config: telegram={cfg['enable_telegram']}, timeout={cfg['probe_timeout']}s")

    init_state_db()
    items = get_vking_items()
    logger.info(f"Found {len(items)} .vking items in Jellyfin DB")

    if not items:
        logger.warning("No .vking items found — nothing to probe")
        return

    alerts = []
    change_report = []
    summary = {"total": len(items), "valid": 0, "invalid": 0, "ratelimited": 0, "changed": 0}

    for item in items:
        item_id = item["id"]
        item_name = item["name"]
        url = item["url"]

        if not url:
            logger.warning(f"  {item_name}: No ShortcutPath URL configured")
            alerts.append(f"⚠ <b>{item_name}</b>: No ShortcutPath URL configured")
            summary["invalid"] += 1
            update_state(item_id, item_name, "", {"valid": False, "error": "No URL", "status_code": 0, "content_type": "", "size": 0})
            continue

        logger.info(f"  Probing {item_name} ({url[:60]}...)")
        result = probe_url(url, cfg["probe_timeout"])
        url_changed, times_changed = update_state(item_id, item_name, url, result)

        if result["valid"] is True:
            logger.info(f"    ✓ Valid — {result['status_code']}, {result['content_type']}, size={result['size']}")
            summary["valid"] += 1
        elif result["valid"] is None:
            logger.info(f"    ⊘ Rate limited — {result['error']}")
            summary["ratelimited"] += 1
        else:
            logger.warning(f"    ✗ Invalid — {result['error']}")
            summary["invalid"] += 1
            alerts.append(f"🔴 <b>{item_name}</b>\nURL: <code>{url[:80]}...</code>\nError: {result['error']}")

        if url_changed:
            summary["changed"] += 1
            change_report.append(f"🔄 <b>{item_name}</b> URL changed (total changes: {times_changed})")

    # Send Telegram alerts
    now_str = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    if cfg["enable_telegram"] and alerts:
        alert_msg = (
            f"🚨 <b>VKing Link Monitor — {len(alerts)} link(s) failed</b>\n\n"
            f"🕐 Checked: {now_str}\n"
            f"📊 Summary: {summary['valid']} valid, {summary['invalid']} invalid, "
            f"{summary['ratelimited']} rate-limited, {summary['changed']} changed\n\n"
            + "\n\n".join(alerts[:10])
        )
        if len(alerts) > 10:
            alert_msg += f"\n\n... and {len(alerts) - 10} more"
        logger.info(f"Sending Telegram alert with {len(alerts)} failure(s)")
        send_telegram(alert_msg, cfg["bot_token"], cfg["chat_id"])
    elif summary["invalid"] > 0 and not cfg["enable_telegram"]:
        logger.warning(f"{summary['invalid']} link(s) failed but Telegram alerts are disabled")

    if cfg["enable_telegram"] and change_report:
        change_msg = (
            f"🔄 <b>VKing Link Monitor — URL changes detected</b>\n\n"
            f"🕐 Checked: {now_str}\n\n"
            + "\n".join(change_report)
        )
        send_telegram(change_msg, cfg["bot_token"], cfg["chat_id"])

    logger.info(
        f"Summary: {summary['total']} total, {summary['valid']} valid, "
        f"{summary['invalid']} invalid, {summary['ratelimited']} rate-limited, "
        f"{summary['changed']} changed"
    )
    logger.info("VKing Link Monitor complete")

if __name__ == "__main__":
    main()
