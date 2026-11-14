#!/usr/bin/env python3
"""
How many times each release has been downloaded, by Jellyfin version, from GitHub's own counts.

    tools/downloads.py

A Jellyfin server installs and updates the plugin by downloading its zip from the release, and updates plugins by
itself unless an admin turns that off. So a release's count, a couple of weeks after it came out, is a rough count of
the servers running it. Rough: a reinstall or a download by hand counts too, and so do our own checks after
publishing (each zip is downloaded once to check its MD5). Nothing is collected from anyone; this only reads what
GitHub shows on the releases page. Reads through `gh`, which releasing needs anyway.
"""
import json
import subprocess

REPOSITORY = "darkejon/jellymold-companion"


def releases():
    result = subprocess.run(["gh", "api", f"repos/{REPOSITORY}/releases?per_page=100"], capture_output=True, text=True, check=True)
    return json.loads(result.stdout)


def build(name):
    """'jellymold-companion_10.0.3.0.zip' → '10.10'; the .md5 files beside the zips are not installs."""
    if not name.endswith(".zip"):
        return None
    version = name.rsplit("_", 1)[-1]
    return "10.10" if version.startswith("10.") else "12" if version.startswith("12.") else None


def main():
    rows = []
    for release in releases():
        counts = {"10.10": 0, "12": 0}
        for asset in release["assets"]:
            if (kind := build(asset["name"])) is not None:
                counts[kind] += asset["download_count"]
        rows.append((release["tag_name"], (release["published_at"] or "")[:10], counts["10.10"], counts["12"]))

    if not rows:
        print("No releases yet.")
        return

    print(f"{'Release':<10}{'Published':<13}{'Jellyfin 10.10':>15}{'Jellyfin 12':>13}{'Both':>7}")
    for tag, published, old, new in rows:
        print(f"{tag:<10}{published:<13}{old:>15}{new:>13}{old + new:>7}")
    print("\nEach count includes our own MD5 check after publishing: one download of each zip.")


if __name__ == "__main__":
    main()
