#!/usr/bin/env python3
"""
VKing Stream Extractor — extracts real MP4 URLs from 1embed.cc.
Called by Jellyfin C# plugin via subprocess.

Portable: auto-detects Chromium and Playwright Python; works on any machine
with Playwright installed. Set env vars to override auto-detection:
    VKING_CHROMIUM   — path to chromium executable
    VKING_COOKIE_FILE — path to cookie JSON (default: alongside this script)
    VKING_PYTHON     — python executable to re-exec into (optional)
"""
import sys
import json
import argparse
import asyncio
import os
import time
import subprocess
from pathlib import Path

# ── Auto-detect paths ──────────────────────────────────────────────
SCRIPT_DIR = Path(__file__).resolve().parent

def _find_chromium() -> str | None:
    env_chromium = os.environ.get("VKING_CHROMIUM")
    if env_chromium and os.path.isfile(env_chromium):
        return env_chromium
    cache_dir = Path.home() / ".cache" / "ms-playwright"
    if cache_dir.exists():
        for chromium_dir in sorted(cache_dir.glob("chromium-*")):
            for sub in ["chrome-linux-arm64/chrome", "chrome-linux/chrome",
                        "chrome-headless-shell-linux-arm64/chrome",
                        "chrome-headless-shell-linux/chrome"]:
                chrome = chromium_dir / sub
                if chrome.exists():
                    return str(chrome)
    for p in ["/usr/bin/chromium", "/usr/bin/chromium-browser",
              "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable"]:
        if os.path.isfile(p):
            return p
    return None

def _find_playwright_python() -> str | None:
    env_python = os.environ.get("VKING_PYTHON")
    if env_python and os.access(env_python, os.X_OK):
        return env_python
    if _has_playwright(sys.executable):
        return sys.executable
    for candidate in [
        "/tmp/playwright-venv/bin/python3",
        str(Path.home() / ".local/share/virtualenvs/playwright/bin/python3"),
    ]:
        if os.path.isfile(candidate) and _has_playwright(candidate):
            return candidate
    return None

def _has_playwright(python_exe: str) -> bool:
    try:
        r = subprocess.run([python_exe, "-c", "import playwright"],
                           capture_output=True, text=True)
        return r.returncode == 0
    except Exception:
        return False

def _subprocess_run(args: list[str]) -> "subprocess.CompletedProcess[str]":
    return subprocess.run(args, capture_output=True, text=True)

# ── Configuration ──────────────────────────────────────────────────
CHROMIUM = _find_chromium()
COOKIE_FILE = os.environ.get("VKING_COOKIE_FILE",
                              str(SCRIPT_DIR / "1embed_cookies.json"))

if CHROMIUM is None:
    print("ERROR: Cannot find Chromium. Set VKING_CHROMIUM or install Playwright: "
          "python3 -m playwright install chromium", file=sys.stderr)
    sys.exit(1)

if not _has_playwright(sys.executable):
    alt = _find_playwright_python()
    if alt and alt != sys.executable:
        print(f"Re-exec under {alt} (Playwright not found in {sys.executable})",
              file=sys.stderr)
        os.execv(alt, [alt] + sys.argv)
    else:
        print("ERROR: Playwright not installed. Install with: "
              "pip install playwright && python3 -m playwright install chromium",
              file=sys.stderr)
        sys.exit(1)

from playwright.async_api import async_playwright

extract_lock = asyncio.Lock()

async def extract_via_browser(browser, tmdb_id, media_type, season, episode, timeout_sec):
    effective = max(timeout_sec - 10, 10)
    url = (f"https://1embed.cc/embed/movie/{tmdb_id}"
           if media_type == "movie"
           else f"https://1embed.cc/embed/tv/{tmdb_id}/{season}/{episode}")
    ctx = await browser.new_context(
        user_agent="Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
                   "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        viewport={"width": 1280, "height": 720})
    if os.path.exists(COOKIE_FILE):
        try:
            with open(COOKIE_FILE) as f:
                ck = json.load(f)
            if ck:
                await ctx.add_cookies(ck)
        except Exception:
            pass
    page = await ctx.new_page()
    mp4_url = mp4_size = 0
    mp4_ct = ""
    async def on_resp(resp):
        nonlocal mp4_url, mp4_size, mp4_ct
        if "workers.dev" not in resp.url or resp.status not in (200, 206):
            return
        ct = resp.headers.get("content-type", "")
        if "video/mp4" not in ct or mp4_url is not None:
            return
        mp4_url = resp.url
        mp4_ct = ct
        try:
            mp4_size = int(resp.headers.get("content-length", 0))
        except Exception:
            pass
        print(f"CAPTURED: {mp4_url[:70]}... size={mp4_size} ct={ct}", file=sys.stderr)
    page.on("response", on_resp)
    print(f"Navigating to {url}...", file=sys.stderr)
    t0 = time.time()
    try:
        resp = await page.goto(url, wait_until="domcontentloaded", timeout=20000)
        if resp is None or resp.status != 200:
            print(f"Page failed: {resp.status if resp else 'None'}", file=sys.stderr)
            await ctx.close()
            return None
    except Exception as e:
        print(f"Navigation error: {e}", file=sys.stderr)
        await ctx.close()
        return None
    print(f"  Loaded in {time.time()-t0:.1f}s", file=sys.stderr)
    await page.wait_for_timeout(2000)
    clicked = False
    for sel in ['button[aria-label="Play"]', 'button[data-action="play"]',
                '.btnPlay', '.play-btn', '.vjs-big-play-button',
                '.controls .play-button', '[class*="play"][type="button"]']:
        try:
            btn = await page.query_selector(sel)
            if btn and (await btn.evaluate("el => getComputedStyle(el).display")) != "none":
                await btn.click()
                print(f"Clicked: {sel}", file=sys.stderr)
                clicked = True
                break
        except Exception:
            pass
    if not clicked:
        print("No play button found, pressing Space", file=sys.stderr)
        try:
            await page.keyboard.press("Space")
        except Exception:
            pass
    await page.wait_for_timeout(1000)
    deadline = time.time() + effective
    while time.time() < deadline:
        await page.wait_for_timeout(500)
        if mp4_url:
            break
        if time.time() > deadline - 5 and not mp4_url:
            try:
                await page.keyboard.press("Space")
            except Exception:
                pass
    await ctx.close()
    if not mp4_url or "video" not in mp4_ct:
        print("No video MP4 captured", file=sys.stderr)
        return None
    duration = 0
    try:
        duration = await page.evaluate(
            "() => { const v = document.querySelector('video'); "
            "return v && v.duration ? Math.round(v.duration*10)/10 : 0; }")
    except Exception:
        pass
    print(f"Done: {mp4_url[:60]}... size={mp4_size} dur={duration}s", file=sys.stderr)
    return {"mp4Url": mp4_url, "size": mp4_size, "duration": duration,
            "content_type": mp4_ct, "accept_ranges": "bytes", "source": "1embed"}

async def extract_async(tmdb_id, media_type, season, episode, timeout_sec):
    print(f"Extracting {media_type} {tmdb_id} (timeout={timeout_sec}s)", file=sys.stderr)
    browser = None
    try:
        async with async_playwright() as p:
            browser = await p.chromium.launch(headless=True, executable_path=CHROMIUM)
            return await extract_via_browser(browser, tmdb_id, media_type, season, episode, timeout_sec)
    except Exception as e:
        print(f"Playwright error: {e}", file=sys.stderr)
        return None
    finally:
        if browser is not None:
            try:
                await browser.close()
            except Exception:
                pass

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--id", required=True)
    parser.add_argument("--type", default="movie", choices=["movie", "tv"])
    parser.add_argument("--timeout", type=int, default=30)
    parser.add_argument("--season", default="1")
    parser.add_argument("--episode", default="1")
    args = parser.parse_args()
    result = asyncio.run(extract_async(args.id, args.type, args.season, args.episode, args.timeout))
    out = {"id": args.id, "type": args.type, "success": False}
    if result:
        out.update({k: result[k] for k in ("success", "mp4Url", "size", "duration",
                                            "content_type", "accept_ranges", "source") if k in result})
        out["success"] = True
    else:
        out["error"] = "No MP4 URL found"
    print(json.dumps(out))

if __name__ == "__main__":
    main()
