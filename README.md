# Jellyfin VidKing Integration

A Jellyfin plugin that resolves `.vking` files (TMDb/IMDb ID pointers) into
streamable media items. Optionally extracts real MP4 URLs from 1Embed.cc
via a headless browser for native Jellyfin playback.

## What it does

- Resolves `.vking` files to playable media items in Jellyfin
- **Tier 1 (optional):** Extracts real MP4 URLs from 1Embed.cc using headless
  Chromium + Playwright, enabling native playback (server-side ffmpeg + all apps)
- **Tier 2:** Stream proxy endpoint for web clients where CORS/client-type
  blocks direct MP4 access
- **Tier 3:** Existing iframe-based web playback as last resort

## Requirements

- Jellyfin 10.11.0+
- .NET 10 SDK (for building from source)

## Installation

### From release package

```bash
# Download the ZIP from GitHub Releases
VERSION="1.0.0.0"
PLUGIN_DIR="/var/lib/jellyfin/plugins/VidKing_${VERSION}"

sudo mkdir -p "$PLUGIN_DIR"
sudo unzip Jellyfin.Plugin.VidKing_${VERSION}.zip -d "$PLUGIN_DIR"
sudo chown -R jellyfin:jellyfin "$PLUGIN_DIR"

# Restart Jellyfin
sudo systemctl restart jellyfin
```

### From source

```bash
git clone https://github.com/zoravar08/jellyfin-vidking-plugin.git
cd jellyfin-vidking-plugin

# Build
bash scripts/build.sh

# Install
VERSION=$(python3 -c "import json; print(json.load(open('src/meta.json'))['version'])")
sudo mkdir -p "/var/lib/jellyfin/plugins/VidKing_${VERSION}"
sudo unzip -o scripts/release/Jellyfin.Plugin.VidKing_${VERSION}.zip \
  -d "/var/lib/jellyfin/plugins/VidKing_${VERSION}"
sudo chown -R jellyfin:jellyfin "/var/lib/jellyfin/plugins/VidKing_${VERSION}"
sudo systemctl restart jellyfin
```

## Plugin Repository

### Third-party repository (auto-install from Jellyfin Catalog)

This is the easiest way for others to install and auto-update the plugin.

1. **Push the repo to GitHub**:
   ```bash
   git remote add origin https://github.com/zoravar08/jellyfin-vidking-plugin.git
   git push -u origin main
   ```

2. **Create a release** on GitHub — tag it `v1.0.0.0` and attach the ZIP:
   ```bash
   git tag v1.0.0.0
   git push origin v1.0.0.0
   ```
   The ZIP must be named `Jellyfin.Plugin.VidKing_1.0.0.0.zip` and uploaded to
   the release. The CI workflow (`.github/workflows/build.yml`) builds and uploads
   it automatically when you create a GitHub Release.

3. **Update the manifest checksum** — and `sourceUrl` in `manifest.json`:
   ```bash
   bash scripts/build.sh
   git add manifest.json
   git commit -m "update manifest checksum"
   git push
   ```
   The build script computes the SHA256 and writes it into `manifest.json`.

4. **Users install** by pasting this URL into Jellyfin:
   **Dashboard → Plugins → Repositories → Add**  
   `https://raw.githubusercontent.com/zoravar08/jellyfin-vidking-plugin/main/manifest.json`

   Then restart Jellyfin. The plugin appears in the Catalog and updates
   automatically when new releases are tagged.

## The Monitor (companion tool)

A separate Python tool that probes all extracted `.vking` URLs for validity,
tracks URL changes, and sends Telegram alerts on failure.

See [monitor/README.md](monitor/README.md) for details.

## Configuration

After installing, go to **Dashboard → Plugin → VidKing Integration** in
Jellyfin's web UI. The plugin auto-detects its config page.

- **Enable Stream Extraction:** Toggle to enable/disable MP4 URL extraction
  (default: on)
- **Extraction Timeout:** Seconds to wait for browser extraction (default: 30)

## How extraction works

1. User opens a `.vking` item in Jellyfin
2. Plugin calls `VKingStreamExtractor.py` which launches a headless Chromium
   browser
3. Browser navigates to the 1Embed.cc embed page, clicks Play
4. Plugin intercepts the `video/mp4` network response from `*.workers.dev`
5. Real MP4 URL is stored in the item's `ShortcutPath` — Jellyfin plays it
   natively via server-side ffmpeg

The Python extractor auto-detects Playwright and Chromium. The user must have
Playwright installed with `playwright install chromium`.

## Release packages

GitHub Releases contain:
- `Jellyfin.Plugin.VidKing.dll` — the plugin
- `VKingStreamExtractor.py` — Python extractor (bundled with DLL)
- `meta.json` — plugin metadata
- `Jellyfin.Plugin.VidKing.deps.json` — dependencies

## Self-Check (development only)

```bash
cd src/SelfCheck/SelfCheck
dotnet run -- /var/lib/jellyfin/data/jellyfin.db
```

NOTE: SelfCheck requires .NET 9 runtime (not installed by default).
The plugin DLL targets net9.0 but builds fine with .NET 10 SDK.

## License

MIT — see [LICENSE](LICENSE).

## Credits

Built for the Jellyfin community. Streams sourced from 1Embed.cc.
