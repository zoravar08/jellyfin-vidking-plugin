#!/usr/bin/env python3
"""
VKing Stream Extractor — extracts real MP4 URLs from a list of vidsrc-family
embed sites (cinesrc.st, vidsrc.st, vidsrc.wiki, ... — see --bases), trying each
in order until one yields a direct link. Called by Jellyfin C# plugin via subprocess.

Environment variables (optional):
    VKING_CHROMIUM  — path to chromium executable (auto-detected if unset)
    VKING_COOKIE_FILE — path to cookie JSON file (default: ./cinesrc_cookies.json)
    VKING_PYTHON     — python executable to re-exec into (auto-detected if unset)
"""
import sys
import json
import argparse
import asyncio
import os
import re
import time
import shutil
from pathlib import Path
from urllib.parse import urlparse

# ── Auto-detect paths ──────────────────────────────────────────────
def _find_chromium() -> str | None:
    """Find the Playwright-installed Chromium executable."""
    # Check env var first
    env_chromium = os.environ.get("VKING_CHROMIUM")
    if env_chromium and os.path.isfile(env_chromium):
        return env_chromium

    # Try to find via Playwright's install path
    try:
        import playwright
        # playwright stores browsers in a cache dir
        # The typical path: ~/.cache/ms-playwright/<version>/chrome-linux-<arch>/chrome
        cache_dir = Path.home() / ".cache" / "ms-playwright"
        if cache_dir.exists():
            for chromium_dir in cache_dir.glob("chromium-*"):
                chrome = chromium_dir / "chrome-linux-arm64" / "chrome"
                if chrome.exists():
                    return str(chrome)
                # Also try chrome-linux (x86_64)
                chrome_x86 = chromium_dir / "chrome-linux" / "chrome"
                if chrome_x86.exists():
                    return str(chrome_x86)
                # Headless shell
                headless = chromium_dir / "chrome-headless-shell-linux-arm64" / "chrome"
                if headless.exists():
                    return str(headless)
                headless_x86 = chromium_dir / "chrome-headless-shell-linux" / "chrome"
                if headless_x86.exists():
                    return str(headless_x86)
    except ImportError:
        pass

    # Fallback: common install locations
    for p in [
        "/usr/bin/chromium",
        "/usr/bin/chromium-browser",
        "/usr/bin/google-chrome",
        "/usr/bin/google-chrome-stable",
    ]:
        if os.path.isfile(p):
            return p

    return None


def _find_python() -> str | None:
    """Find a Python executable with Playwright installed."""
    env_python = os.environ.get("VKING_PYTHON")
    if env_python and os.access(env_python, os.X_OK):
        return env_python

    # Check current executable
    if _has_playwright(sys.executable):
        return sys.executable

    # Search common venv locations
    for candidate in [
        "/tmp/playwright-venv/bin/python3",
        "/opt/playwright/venv/bin/python3",
        str(Path.home() / ".local" / "share" / "virtualenvs" / "playwright" / "bin" / "python3"),
    ]:
        if os.path.isfile(candidate) and _has_playwright(candidate):
            return candidate

    return None


def _has_playwright(python_exe: str) -> bool:
    """Check if a python executable has the playwright module."""
    try:
        proc = subprocess_run([python_exe, "-c", "import playwright"])
        return proc.returncode == 0
    except Exception:
        return False


def subprocess_run(args: list[str]) -> "subprocess.CompletedProcess[str]":
    import subprocess
    return subprocess.run(args, capture_output=True, text=True)


# ── Configuration ──────────────────────────────────────────────────
CHROMIUM = _find_chromium()
COOKIE_FILE = os.environ.get("VKING_COOKIE_FILE",
                              str(Path(__file__).parent / "cinesrc_cookies.json"))

if CHROMIUM is None:
    print("ERROR: Cannot find Chromium. Set VKING_CHROMIUM or install Playwright: "
          "python3 -m playwright install chromium", file=sys.stderr)
    sys.exit(1)

# Ensure we're running under a Python that has Playwright
if not _has_playwright(sys.executable):
    alt = _find_python()
    if alt and alt != sys.executable:
        print(f"Re-executing under {alt} (Playwright not found in {sys.executable})",
              file=sys.stderr)
        os.execv(alt, [alt] + sys.argv)
    else:
        print("ERROR: Playwright not installed. Install with: "
              "pip install playwright && python3 -m playwright install chromium",
              file=sys.stderr)
        sys.exit(1)

# Import after exec check so we're guaranteed to have it
from playwright.async_api import async_playwright

extract_lock = asyncio.Lock()

# Movie embeds are `/embed/movie/{id}` everywhere in this vidsrc-clone family,
# but TV URL shape isn't - cinesrc.st moved to query params, confirmed by curl
# (path form 404s, query form 200s); vidsrc.st/vidsrc.wiki still use the path
# form. Keyed by netloc; unlisted sites default to the path form.
TV_URL_QUERY_PARAM_HOSTS = {"cinesrc.st"}


def _tv_embed_url(base, tmdb_id, season, episode):
    host = urlparse(base).netloc
    if host in TV_URL_QUERY_PARAM_HOSTS:
        return f"{base}/embed/tv/{tmdb_id}?s={season}&e={episode}"
    return f"{base}/embed/tv/{tmdb_id}/{season}/{episode}"


# bcine.ru isn't a vidsrc-family clone: no /embed/{type}/{id} URL, no uniform
# play-button selector, and it serves a per-title mix of direct MP4 and HLS
# (.m3u8) instead of always workers.dev MP4. It gets its own nav/capture flow
# below (extract_from_bcine) instead of extract_from_base's assumptions.
BCINE_HOST = "bcine.ru"


def _is_bcine(base):
    return BCINE_HOST in urlparse(base).netloc


async def extract_from_bcine(browser, tmdb_id, media_type, season, episode, timeout_sec):
    """bcine.ru flow: /movie/{id} + "PLAY" click, or /tv/{id} + SEASON/EPISODE
    expand + season-dropdown select (multi-season only) + episode-row expand
    + inline "Play" click. Captures the first direct MP4 or HLS master
    response from the kupal.bingey.cfd CDN.
    """
    effective = max(timeout_sec - 10, 10)
    url = f"https://bcine.ru/{'movie' if media_type == 'movie' else 'tv'}/{tmdb_id}"

    ctx = await browser.new_context(viewport={"width": 1280, "height": 900})
    page = await ctx.new_page()

    media_url = None
    media_ct = ""

    async def on_resp(resp):
        nonlocal media_url, media_ct
        if media_url is not None:
            return
        if resp.status not in (200, 206):
            return
        u = resp.url
        if "bingey" not in u and "kupal" not in u:
            return
        ct = resp.headers.get("content-type", "")
        is_mp4 = "video/mp4" in ct
        is_m3u8 = u.endswith(".m3u8") or "mpegurl" in ct
        if not (is_mp4 or is_m3u8):
            return
        media_url = u
        media_ct = "application/vnd.apple.mpegurl" if is_m3u8 else ct
        print(f"CAPTURED: {media_url[:70]}... ct={media_ct}", file=sys.stderr)

    page.on("response", on_resp)

    print(f"Navigating to {url}...", file=sys.stderr)
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

    await page.wait_for_timeout(2500)

    try:
        if media_type == "movie":
            await page.get_by_text("PLAY", exact=True).first.click(timeout=5000)
        else:
            season_n = int(season or 1)
            episode_n = int(episode or 1)

            await page.get_by_text("SEASON/EPISODE", exact=False).first.click(timeout=5000)
            await page.wait_for_timeout(1200)

            if season_n != 1:
                # The season dropdown only exists on multi-season shows; a
                # single-season show has no dropdown, so a failed click here
                # is expected and not fatal — S1 is already what's listed.
                try:
                    await page.get_by_role("button", name=re.compile(r"^Season \d")).first.click(timeout=3000)
                    await page.wait_for_timeout(500)
                    await page.get_by_text(f"Season {season_n}", exact=True).first.click(timeout=3000)
                    await page.wait_for_timeout(1200)
                except Exception as e:
                    print(f"season dropdown select failed (single-season show?): {e}", file=sys.stderr)

            # Episode rows are accordion-toggle buttons (thumbnail img +
            # chevron-down icon); the Nth one is episode N. Expand it, then
            # click the inline lowercase "Play" revealed inside — the
            # page-level uppercase "PLAY" always plays the
            # default/continue-watching episode regardless of row selection.
            #
            # The list is client-hydrated after the SEASON/EPISODE panel opens, so a
            # single fixed wait races the render under load (observed live: Zero Day
            # found 0 rows at ~1.2s, found all 6 on an immediate solo retest) - poll a
            # few times instead of trusting one snapshot.
            el = None
            for attempt in range(4):
                ep_btn = await page.evaluate_handle(
                    """(idx) => {
                        const btns = Array.from(document.querySelectorAll('button'))
                            .filter(b => b.querySelector('img') && b.querySelector('svg.lucide-chevron-down'));
                        return btns[idx] || null;
                    }""",
                    episode_n - 1,
                )
                el = ep_btn.as_element()
                if el is not None:
                    break
                print(f"episode row {episode_n} not rendered yet (attempt {attempt + 1}/4)", file=sys.stderr)
                await page.wait_for_timeout(1000)

            if el is None:
                print(f"episode row {episode_n} not found", file=sys.stderr)
                await ctx.close()
                return None
            await el.scroll_into_view_if_needed()
            await el.click()
            await page.wait_for_timeout(1000)
            await page.get_by_role("button", name="Play", exact=True).first.click(timeout=5000)
    except Exception as e:
        print(f"play click failed: {e}", file=sys.stderr)
        await ctx.close()
        return None

    deadline = time.time() + effective
    while time.time() < deadline and media_url is None:
        await page.wait_for_timeout(500)

    await ctx.close()

    if media_url is None:
        print("No media URL captured on bcine.ru", file=sys.stderr)
        return None

    print(f"Done: {media_url[:60]}... ct={media_ct}", file=sys.stderr)
    return {
        "mp4Url": media_url,
        "size": 0,
        "duration": 0,
        "content_type": media_ct,
        "accept_ranges": "bytes",
        "source": "bcine.ru",
    }


async def extract_from_base(browser, base, tmdb_id, media_type, season, episode, timeout_sec):
    """Navigate to one embed site, click play, capture its worker MP4 URL."""
    effective = max(timeout_sec - 10, 10)
    base = base.rstrip("/")
    url = (
        f"{base}/embed/movie/{tmdb_id}"
        if media_type == "movie"
        else _tv_embed_url(base, tmdb_id, season, episode)
    )

    ctx = await browser.new_context(
        user_agent=(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        ),
        viewport={"width": 1280, "height": 720},
    )

    # Load any saved cookies
    if os.path.exists(COOKIE_FILE):
        try:
            with open(COOKIE_FILE) as f:
                cookies = json.load(f)
            if cookies:
                await ctx.add_cookies(cookies)
        except Exception:
            pass

    page = await ctx.new_page()
    mp4_url = None
    mp4_size = 0
    mp4_ct = ""

    # Register response handler BEFORE navigation
    async def on_resp(resp):
        nonlocal mp4_url, mp4_size, mp4_ct
        if "workers.dev" not in resp.url:
            return
        if resp.status not in (200, 206):
            return
        ct = resp.headers.get("content-type", "")
        if "video/mp4" not in ct:
            return
        if mp4_url is not None:
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

    # Click play
    clicked = False
    for sel in ['button[aria-label="Play"]', 'button[data-action="play"]',
                '.btnPlay', '.play-btn', '.vjs-big-play-button',
                '.controls .play-button', '[class*="play"][type="button"]']:
        try:
            btn = await page.query_selector(sel)
            if btn:
                display = await btn.evaluate("el => getComputedStyle(el).display")
                if display != "none":
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

    # Wait for MP4 capture
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
            "return v && v.duration ? Math.round(v.duration*10)/10 : 0; }"
        )
    except Exception:
        pass

    print(f"Done: {mp4_url[:60]}... size={mp4_size} dur={duration}s", file=sys.stderr)
    return {
        "mp4Url": mp4_url,
        "size": mp4_size,
        "duration": duration,
        "content_type": mp4_ct,
        "accept_ranges": "bytes",
        "source": urlparse(base).netloc,
    }


async def extract_async(tmdb_id, media_type, season, episode, timeout_sec, bases):
    """Try each base in order, same browser instance, stop at the first MP4 capture."""
    print(f"Extracting {media_type} {tmdb_id} (timeout={timeout_sec}s, bases={bases})",
          file=sys.stderr)
    browser = None
    try:
        async with async_playwright() as p:
            browser = await p.chromium.launch(
                headless=True,
                executable_path=CHROMIUM,
            )
            # Split the overall timeout across candidates so a slow/dead first source
            # doesn't eat the whole budget and starve the rest.
            per_base_timeout = max(timeout_sec // max(len(bases), 1), 10)
            for i, base in enumerate(bases):
                print(f"--- Source {i + 1}/{len(bases)}: {base} ---", file=sys.stderr)
                result = (
                    await extract_from_bcine(
                        browser, tmdb_id, media_type, season, episode, per_base_timeout
                    )
                    if _is_bcine(base)
                    else await extract_from_base(
                        browser, base, tmdb_id, media_type, season, episode, per_base_timeout
                    )
                )
                if result:
                    return result
            return None
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
    parser.add_argument("--bases", default="https://cinesrc.st",
                         help="comma-separated embed sites to try in order")
    args = parser.parse_args()

    bases = [b.strip() for b in args.bases.split(",") if b.strip()]

    result = asyncio.run(
        extract_async(args.id, args.type, args.season, args.episode, args.timeout, bases)
    )

    output = {"id": args.id, "type": args.type, "success": False}
    if result:
        output["success"] = True
        output["mp4Url"] = result["mp4Url"]
        output["size"] = result["size"]
        output["duration"] = result["duration"]
        output["content_type"] = result["content_type"]
        output["accept_ranges"] = result["accept_ranges"]
        output["source"] = result["source"]
    else:
        output["error"] = "No MP4 URL found"

    print(json.dumps(output))


if __name__ == "__main__":
    main()
