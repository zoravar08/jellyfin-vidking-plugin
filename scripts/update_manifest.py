#!/usr/bin/env python3
"""
Update the Jellyfin plugin repository manifest.json with SHA256 checksum
and download URL for the built release ZIP.

Usage:
    python3 scripts/update_manifest.py <zip_path> <download_url>
"""
import json
import sys
import hashlib
import os


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(8192), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> None:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <zip_path> <download_url>")
        sys.exit(1)

    zip_path = sys.argv[1]
    download_url = sys.argv[2]

    checksum = sha256_file(zip_path)
    print(f"SHA256:   {checksum}")
    print(f"Download: {download_url}")

    # zip_path is like /path/to/repo/scripts/release/file.zip
    # repo root is three dirname levels up
    repo_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(zip_path))))
    manifest_path = os.path.join(repo_root, "manifest.json")
    with open(manifest_path) as f:
        manifest = json.load(f)

    for plugin in manifest:
        for ver in plugin["versions"]:
            ver["sourceUrl"] = download_url
            ver["checksum"] = checksum

    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=1)
        f.write("\n")

    print("manifest.json updated")


if __name__ == "__main__":
    main()
